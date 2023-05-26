using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents.DocumentStructures;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace VideoProcessing
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        // 路径
        string _raw_fullname = null;
        string _output_fullname = null;

        // 进度控制
        double _current_frame_pos = 0;
        bool _canChangeSlider = true;

        // 读写资源锁
        readonly object _readLocker = new object();
        readonly object _writeLocker = new object();

        // 状态标记
        bool _processing = false;
        Stopwatch _watcher = new Stopwatch();

        // 视频信息
        VideoCapture _cap = null;
        double _raw_fps = 0;
        double _raw_width = 0;
        double _raw_height = 0;
        double _raw_fourcc = 0;
        double _raw_totalFrameCount = 0;
        double _raw_totalTimeSecs = 0;
        VideoWriter _writer = null;

        // 后台预览任务
        OneTimeTask _backgroundTask = null;

        // 处理参数（此处采用反比例函数模型进行处理）
        // m,n的值由反比例函数模型b=n/(c+m)得到，其中，b为亮度，c为对比度。
        // 解方程组 【亮度处理上限】=n/(【对比度处理下限】)+m) & 【亮度处理上限】=n/(对比度处理下限】+m)，可得m，n.
        // 注，该方程组由于对反比例函数进行了横向缩放和平移，因此，计算出的c最终可能<0，为避免该情况，求得的对比度小于1时，使用1进行处理.
        double PARA_THRESHOLD_BRIGHTNESS_MAX = 130;     //【亮度处理上限】。画面中曝光较好不用处理的帧的亮度，高于该亮度将直接使用原始帧。
        double PARA_THRESHOLD_BRIGHTNESS_MIN = 70;      //【亮度处理下限】。画面中曝光较差且帧数较多的帧的亮度，
        double PARA_COMPENSATION_CONTRAST_MAX = 2.65;   //【对比度处理上限】。将【亮度处理下限】帧提升到较好的画质需要提升的对比度系数。
        double PARA_COMPENSATION_CONTRAST_MIN = 1;      //【对比度处理下限】。一般情况下应为常数1。
        double PARA_THRESHOLD_BRIGHTNESS_MAX_DEFAULT = 130;     //【亮度处理上限】默认值
        double PARA_THRESHOLD_BRIGHTNESS_MIN_DEFAULT = 70;      //【亮度处理下限】默认值
        double PARA_COMPENSATION_CONTRAST_MAX_DEFAULT = 2.65;   //【对比度处理上限】默认值
        double PARA_COMPENSATION_CONTRAST_MIN_DEFAULT = 1;      //【对比度处理下限】默认值
        double M;
        double N;

        public MainWindow()
        {
            InitializeComponent();

            (M, N) = SolveEquations(PARA_THRESHOLD_BRIGHTNESS_MIN, PARA_COMPENSATION_CONTRAST_MAX, PARA_THRESHOLD_BRIGHTNESS_MAX, PARA_COMPENSATION_CONTRAST_MIN);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            slider_brigtness_max.ValueChanged += slider_brigtness_max_ValueChanged;
            slider_brigtness_min.ValueChanged += slider_brigtness_min_ValueChanged;
            slider_contrast_max.ValueChanged += slider_contrast_max_ValueChanged;
            slider_contrast_min.ValueChanged += slider_contrast_min_ValueChanged;
            slider_brigtness_max.Value = PARA_THRESHOLD_BRIGHTNESS_MAX;
            slider_brigtness_min.Value = PARA_THRESHOLD_BRIGHTNESS_MIN;
            slider_contrast_max.Value = PARA_COMPENSATION_CONTRAST_MAX;
            slider_contrast_min.Value = PARA_COMPENSATION_CONTRAST_MIN;
        }

        /// <summary>
        /// 打开文件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btn_openfile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            dialog.CheckFileExists = true;
            dialog.AddExtension = true;
            dialog.Filter = "仅支持mp4或avi视频|*.mp4;*.avi";
            if (dialog.ShowDialog() == true)
            {
                // 文件路径处理
                _raw_fullname = dialog.FileName;
                var dir = Path.GetDirectoryName(dialog.FileName);
                var shortname = Path.GetFileNameWithoutExtension(dialog.FileName);
                var ext = Path.GetExtension(dialog.FileName);
                _output_fullname = Path.Combine(dir, $"{shortname}_processed{ext}");
                tb_raw_fullname.Text = _raw_fullname;

                // 读取视频详细信息，并初始化写入信息
                _cap?.Release();
                _writer?.Release();
                _cap = new VideoCapture(_raw_fullname);
                _raw_fps = _cap.Get(VideoCaptureProperties.Fps);
                _raw_width = _cap.Get(VideoCaptureProperties.FrameWidth);
                _raw_height = _cap.Get(VideoCaptureProperties.FrameHeight);
                _raw_fourcc = _cap.Get(VideoCaptureProperties.FourCC);
                _raw_totalFrameCount = _cap.Get(VideoCaptureProperties.FrameCount);
                _raw_totalTimeSecs = _raw_totalFrameCount / _raw_fps;
                slider.Maximum = _raw_totalFrameCount;
                tb_total_frame.Text = _raw_totalFrameCount.ToString();
                tb_total_time.Text = TimeSpan.FromSeconds(_raw_totalTimeSecs).ToString(@"hh\:mm\:ss\.fff");

                // 处理第0帧
                slider.Value = 0;
                HandleAFrame(true, false);
            }
        }

        /// <summary>
        /// 开始预览
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btn_preview_Click(object sender, RoutedEventArgs e)
        {
            if (_cap == null)
            {
                return;
            }

            if (btn_preview.Tag == null)
            {
                // 控制状态改变
                btn_preview.Content = "暂停";
                btn_preview.Tag = 1;
                btn_openfile.IsEnabled = false;
                btn_process.IsEnabled = false;
                tb_status.Visibility = Visibility.Visible;
                tb_status.Text = "预览中";

                _backgroundTask?.Cancel();
                _backgroundTask = new OneTimeTask(() =>
                {
                    HandleAFrame(true, false);
                });
                _backgroundTask.Start();
            }
            else
            {
                _backgroundTask?.Cancel();

                btn_preview.Content = "预览";
                btn_preview.Tag = null;
                btn_openfile.IsEnabled = true;
                btn_process.IsEnabled = true;
                tb_status.Visibility = Visibility.Collapsed;
                tb_status.Text = "";
            }
        }

        /// <summary>
        /// 开始处理
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btn_process_Click(object sender, RoutedEventArgs e)
        {
            if (_cap == null)
            {
                return;
            }


            if (btn_process.Tag == null)
            {
                if (File.Exists(_output_fullname))
                {
                    if (MessageBox.Show(this, "目标文件已存在，处理过程将会覆盖该文件，是否继续？", "提示", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
                    {
                        return;
                    }
                }

                slider.Value = 0;

                // 控制状态改变
                btn_process.Content = "停止";
                btn_process.Tag = 1;
                btn_openfile.IsEnabled = false;
                btn_preview.IsEnabled = false;
                slider.IsEnabled = false;
                bd_settings.IsEnabled = false;
                _processing = true;
                _watcher.Restart();
                tb_status.Visibility = Visibility.Visible;
                tb_status.Text = "已处理 0% 预计还需 0 分钟";

                // 初始化输出对象
                _writer = new VideoWriter(_output_fullname, (FourCC)_raw_fourcc, _raw_fps, new OpenCvSharp.Size(_raw_width, _raw_height));

                _backgroundTask?.Cancel();
                _backgroundTask = new OneTimeTask(() =>
                {
                    HandleAFrame(true, true);
                });
                _backgroundTask.Start();
            }
            else
            {
                _backgroundTask?.Cancel();

                btn_process.Content = "处理";
                btn_process.Tag = null;
                btn_openfile.IsEnabled = true;
                btn_preview.IsEnabled = true;
                slider.IsEnabled = true;
                bd_settings.IsEnabled = true;
                _processing = false;
                _watcher.Stop();
                tb_status.Visibility = Visibility.Collapsed;
                tb_status.Text = "";

                // 释放化输出对象
                _writer.Release();
            }
        }

        /// <summary>
        /// 处理一帧
        /// </summary>
        /// <param name="doDisplay"></param>
        /// <param name="doWrite"></param>
        private void HandleAFrame(bool doDisplay, bool doWrite)
        {
            // 读取并处理当前帧
            var img1 = new Mat();
            lock (_readLocker)
            {
                _cap.Set(VideoCaptureProperties.PosFrames, _current_frame_pos);
                _cap.Read(img1);
                _current_frame_pos++;
            }

            if (img1.Empty())
            {
                return;
            }

            // 灰度图
            var gray1 = new Mat();
            Cv2.CvtColor(img1, gray1, ColorConversionCodes.BGR2GRAY);

            // 处理得到新帧
            var img2 = GetNewFrame(img1);

            // 写入
            if (doWrite)
            {
                _writer?.Write(img2);
            }

            // 进度
            double proc = Math.Round(_current_frame_pos / _raw_totalFrameCount * 100, 2);
            var minRest = (int)((_raw_totalFrameCount - _current_frame_pos) / _current_frame_pos * _watcher.Elapsed.TotalSeconds / 60);

            // 显示
            if (doDisplay)
            {
                // 左
                var bright1 = GetBrightness(img1);
                var bitmap1 = doDisplay ? img1.ToWriteableBitmap().ToBitmapImage() : null;

                //// 右
                var bright2 = GetBrightness(img2);
                var bitmap2 = doDisplay ? img2.ToWriteableBitmap().ToBitmapImage() : null;

                if (Application.Current != null)
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        img_raw.Source = bitmap1;
                        img_output.Source = bitmap2;
                        tb_brightess_raw.Text = bright1.ToString();
                        tb_brightess_output.Text = bright2.ToString();
                        tb_current_frame.Text = _current_frame_pos.ToString();
                        tb_current_time.Text = TimeSpan.FromSeconds(_current_frame_pos / _raw_fps).ToString(@"hh\:mm\:ss\.fff");

                        // 滑动条
                        if (_canChangeSlider)
                        {
                            slider.Value = _current_frame_pos;
                        }

                        // 处理进度
                        if (_processing)
                        {
                            tb_status.Text = $"已处理 {proc}% 还需约 {minRest} 分钟";
                        }
                    }));
                }
            }
        }

        /// <summary>
        /// 获取图像平均亮度
        /// </summary>
        /// <param name="mat"></param>
        /// <returns></returns>
        private double GetBrightness(Mat mat)
        {
            var gray = new Mat();
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
            var brightness = Cv2.Mean(mat);
            return Math.Round(brightness.Val0, 3);
        }

        /// <summary>
        /// 根据反比例函数平滑处理图像对比图,并保证最低亮度
        /// </summary>
        /// <param name="img"></param>
        /// <returns></returns>
        private Mat GetNewFrame(Mat img)
        {
            var b = GetBrightness(img);

            if (b >= PARA_THRESHOLD_BRIGHTNESS_MAX)
            {
                return img;
            }

            // 对比度调整
            Mat img2 = new Mat();
            var tempC = (N / b) - M;
            var c = tempC > PARA_COMPENSATION_CONTRAST_MIN ? tempC : PARA_COMPENSATION_CONTRAST_MIN;
            img.ConvertTo(img2, MatType.CV_8UC3, b <= PARA_THRESHOLD_BRIGHTNESS_MIN ? PARA_COMPENSATION_CONTRAST_MAX : c, 0);
            var b2 = GetBrightness(img2);
            if (b2 > PARA_THRESHOLD_BRIGHTNESS_MAX)
            {
                return img2;
            }

            // 保底亮度
            Mat img3 = new Mat();
            img2.ConvertTo(img3, MatType.CV_8UC3, 1, (PARA_THRESHOLD_BRIGHTNESS_MAX - b2));
            return img3;
        }

        /// <summary>
        /// 滑条值改变
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _current_frame_pos = e.NewValue;
            tb_current_frame.Text = ((int)e.NewValue).ToString();
            if (_raw_fps != 0)
            {
                tb_current_time.Text = TimeSpan.FromSeconds(e.NewValue / _raw_fps).ToString(@"hh\:mm\:ss\.fff");
            }
        }

        /// <summary>
        /// 滑条拖曳开始
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void slider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _canChangeSlider = false;
        }

        /// <summary>
        /// 滑条拖曳结束
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void slider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _canChangeSlider = true;
        }

        bool _firstKepDown = true;
        /// <summary>
        /// 全局按键
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_processing)
            {
                return;
            }

            if (!Keyboard.IsKeyDown(Key.Left) && !Keyboard.IsKeyDown(Key.Right))
            {
                return;
            }

            int step = 10;
            string animStr = "anim_ff";
            string imgStr = "/Resources/fastforward.png";
            string txt = "+";

            if (Keyboard.IsKeyDown(Key.Left))
            {
                step = 10;
                imgStr = "/Resources/rewind.png";
                txt = "-";
                animStr = "anim_ff";

                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                {
                    step = 30;
                    animStr = "anim_ff_2";
                }

                if (_current_frame_pos <= step)
                {
                    _current_frame_pos = 0;
                }
                else
                {
                    _current_frame_pos -= step;
                }
            }
            else if (Keyboard.IsKeyDown(Key.Right))
            {
                step = 10;
                imgStr = "/Resources/fastforward.png";
                txt = "+";
                animStr = "anim_ff";

                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                {
                    step = 30;
                    animStr = "anim_ff_2";
                }

                if (_current_frame_pos >= _raw_totalFrameCount - step)
                {
                    _current_frame_pos = _raw_totalFrameCount;
                }
                else
                {
                    _current_frame_pos += step;
                }
            }

            slider.Value = _current_frame_pos;

            // 动画
            if (_firstKepDown)
            {
                _firstKepDown = false;
                stack_ff.Visibility = Visibility.Visible;
                tb_ff.Text = $"{txt}{step}";

                // 图像切换
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(imgStr, UriKind.Relative);
                image.EndInit();
                img_ff.Source = image;

                var anim = (Storyboard)this.FindResource(animStr);
                anim.Begin();
            }

        }

        /// <summary>
        /// 全局取消按键
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            _canChangeSlider = true;
            _firstKepDown = true;
            stack_ff.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 解反比例方程b=n/(c+m)
        /// </summary>
        /// <returns></returns>
        private (double, double) SolveEquations(double b1, double c1, double b2, double c2)
        {
            var m = (b2 * c2 - b1 * c1) / (b1 - b2);
            var n = b1 * m + b1 * c1;
            return (m, n);
        }

        /// <summary>
        /// 关闭程序，释放资源
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _backgroundTask?.Cancel();
            _writer?.Release();
            _cap?.Release();
        }


        #region 参数设置

        private void slider_brigtness_max_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PARA_THRESHOLD_BRIGHTNESS_MAX = Math.Round(e.NewValue, 2);
            tb_brightness_max.Text = PARA_THRESHOLD_BRIGHTNESS_MAX.ToString();
            (M, N) = SolveEquations(PARA_THRESHOLD_BRIGHTNESS_MIN, PARA_COMPENSATION_CONTRAST_MAX, PARA_THRESHOLD_BRIGHTNESS_MAX, PARA_COMPENSATION_CONTRAST_MIN);
        }

        private void slider_contrast_min_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PARA_COMPENSATION_CONTRAST_MIN = Math.Round(e.NewValue, 2);
            tb_contrast_min.Text = PARA_COMPENSATION_CONTRAST_MIN.ToString();
            (M, N) = SolveEquations(PARA_THRESHOLD_BRIGHTNESS_MIN, PARA_COMPENSATION_CONTRAST_MAX, PARA_THRESHOLD_BRIGHTNESS_MAX, PARA_COMPENSATION_CONTRAST_MIN);
        }

        private void slider_brigtness_min_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PARA_THRESHOLD_BRIGHTNESS_MIN = Math.Round(e.NewValue, 2);
            tb_brightness_min.Text = PARA_THRESHOLD_BRIGHTNESS_MIN.ToString();
            (M, N) = SolveEquations(PARA_THRESHOLD_BRIGHTNESS_MIN, PARA_COMPENSATION_CONTRAST_MAX, PARA_THRESHOLD_BRIGHTNESS_MAX, PARA_COMPENSATION_CONTRAST_MIN);
        }

        private void slider_contrast_max_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PARA_COMPENSATION_CONTRAST_MAX = Math.Round(e.NewValue, 2);
            tb_contrast_max.Text = PARA_COMPENSATION_CONTRAST_MAX.ToString();
            (M, N) = SolveEquations(PARA_THRESHOLD_BRIGHTNESS_MIN, PARA_COMPENSATION_CONTRAST_MAX, PARA_THRESHOLD_BRIGHTNESS_MAX, PARA_COMPENSATION_CONTRAST_MIN);
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            slider_brigtness_max.Value = PARA_THRESHOLD_BRIGHTNESS_MAX_DEFAULT;
            slider_brigtness_min.Value = PARA_THRESHOLD_BRIGHTNESS_MIN_DEFAULT;
            slider_contrast_max.Value = PARA_COMPENSATION_CONTRAST_MAX_DEFAULT;
            slider_contrast_min.Value = PARA_COMPENSATION_CONTRAST_MIN_DEFAULT;
        }

        #endregion

    }
}
