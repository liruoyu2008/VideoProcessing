using System;
using System.Threading;
using System.Threading.Tasks;

namespace VideoProcessing
{
    /// <summary>
    /// 一次性后台定时任务.
    /// </summary>
    public class OneTimeTask
    {
        public bool Token { get; private set; }

        public Task Task { get; private set; }

        public bool Used { get; private set; }


        /// <summary>
        /// 使用自有令牌的一次性任定时任务，不会出现常见的因令牌复用而产生的任务堆叠问题.
        /// 使用方法：
        /// 1.实例化并传入委托和该委托的执行间隔;
        /// 2.start开始;
        /// 3.cancel结束(不会中断已经开始执行的委托，但会阻止其进入下一轮执行).
        /// 4.需要重新开始任务，请使用新的实例.
        /// </summary>
        /// <param name="action">需要执行的委托.</param>
        /// <param name="interval">执行间隔(ms).</param>
        /// <exception cref="ArgumentNullException"></exception>
        public OneTimeTask(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException("argument 'action' cannot be null.");
            }

            Task = new Task(() =>
            {
                while (Token)
                {
                    try
                    {
                        action.Invoke();
                    }
                    catch (Exception) { }
                }
            });
        }

        /// <summary>
        /// 开始任务.
        /// </summary>
        /// <exception cref="Exception">重复使用会抛出异常.</exception>
        public void Start()
        {
            if (!Used)
            {
                Token = true;
                Task.Start();
            }
            else
            {
                throw new Exception("One-Time task cannot be reused.");
            }
        }

        /// <summary>
        /// 取消任务(若本轮任务已开始，则无法中断，但能组织其进入下一轮任务).
        /// </summary>
        public void Cancel()
        {
            Token = false;
        }
    }
}
