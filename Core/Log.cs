

using System.Text;

namespace SkyrimJPStringPatcher.Core
{
    public class Log :IDisposable
    {
        public static Log Instance = new Log();

        private readonly StreamWriter _writer;

        private object _lock = new object();

        public Log()
        {
            _writer = new StreamWriter("app.log",true,Encoding.UTF8);
        }
        public void Stage(string stage)
        {
            lock (_lock)
            {
                _writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "->" + stage);
            }
        }

        public void Info(string message)
        {
            lock (_lock)
            {
                _writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "->" + message);
            }
        }

        public void Debug(string message)
        {
            lock (_lock)
            {
                _writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "->" + message);
            }
        }

        public void Warning(string message)
        {
            lock (_lock)
            {
                _writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "->" + message);
            }
        }

        public void Error(string message)
        {
            lock (_lock)
            {
                _writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "->" + message);
            }
        }

        public void Dispose()
        {
            _writer.Close();
        }
    }
}
