using System.Diagnostics;
using System.Text;
using NLog;

namespace Meshmakers.Octo.Common.Shared;

/// <summary>
///     Performance manager implementation
/// </summary>
public class PerfManager
{
    private static PerfManager? _inst;
    private readonly Dictionary<string, PerfItem> _items;
    private readonly char _separator = ';';

    private PerfManager()
    {
        _items = new Dictionary<string, PerfItem>();
    }

    /// <summary>
    ///     Returns the single instance of the performance manager
    /// </summary>
    public static PerfManager Instance => _inst ??= new PerfManager();

    internal PerfItem CreateMeasurement(string methodName)
    {
        if (_items.ContainsKey(methodName)) return _items[methodName];

        var itm = new PerfItem(methodName);
        _items.Add(methodName, itm);
        return itm;
    }

    /// <summary>
    ///     Writes logging information to a logger
    /// </summary>
    /// <param name="logger"></param>
    //   [Conditional("PERF")]
    public void WriteLogging(Logger logger)
    {
        logger.Info("Performance analysis");
        logger.Info(WriteStatistics());
    }

    private string WriteStatistics()
    {
        var stringBuilder = new StringBuilder();
        // Header
        var x = "Full member name" + _separator;
        x += "Checkpoint (cp)" + _separator;
        x += "Total exec." + _separator;
        x += "Time 1st run" + _separator;
        x += "Total time 1st run exec." + _separator;
        x += "1st run exec. prev. cp" + _separator;
        x += "Average to prev. cp" + _separator;
        x += "Average to prev. cp (1st run ignored)" + _separator;
        x += "Execution time" + _separator;
        stringBuilder.AppendLine(x);

        foreach (var item in _items.Values) item.WriteToStream(stringBuilder);

        return stringBuilder.ToString();
    }
}

/// <summary>
///     Represents a performance item that summarizes performance information
/// </summary>
public class PerfItem
{
    private readonly Dictionary<string, List<PerfLogItem>> _result;
    private readonly char _separator = ';';
    private readonly Stopwatch _stopwatch;
    private readonly Stopwatch _stopwatchCp;

    internal PerfItem(string methodName)
    {
        MethodName = methodName;
        _result = new Dictionary<string, List<PerfLogItem>>();
        _stopwatch = new Stopwatch();
        _stopwatchCp = new Stopwatch();
    }

    /// <summary>
    ///     Returns the method name
    /// </summary>
    private string MethodName { get; }

    internal void Start()
    {
        WriteMessage("Started");
        _stopwatch.Reset();
        _stopwatchCp.Reset();
        _stopwatch.Start();
        _stopwatchCp.Start();
    }

    internal void SetCheckPoint(string strDescription)
    {
        _stopwatch.Stop();
        _stopwatchCp.Stop();
        WriteMessage("Checkpoint \"{0}\" at {1} ms", strDescription, _stopwatch.ElapsedMilliseconds);
        SetLog(strDescription, _stopwatchCp.ElapsedMilliseconds, _stopwatch.ElapsedMilliseconds, DateTime.Now);
        _stopwatchCp.Reset();
        _stopwatch.Start();
        _stopwatchCp.Start();
    }

    private void Stop(string strDescription)
    {
        _stopwatch.Stop();
        _stopwatchCp.Stop();
        WriteMessage("Finished \"{0}\" at {1} ms", strDescription, _stopwatch.ElapsedMilliseconds);
        SetLog(strDescription, _stopwatchCp.ElapsedMilliseconds, _stopwatch.ElapsedMilliseconds, DateTime.Now);
    }

    internal void Stop()
    {
        Stop("Finished");
    }

    private void SetLog(string strCheckPoint, long nElapsedCheckpoint, long nElapsed, DateTime dt)
    {
        List<PerfLogItem> lst;
        if (_result.ContainsKey(strCheckPoint))
        {
            lst = _result[strCheckPoint];
        }
        else
        {
            lst = new List<PerfLogItem>();
            _result.Add(strCheckPoint, lst);
        }

        lst.Add(new PerfLogItem(nElapsed, nElapsedCheckpoint, dt));
    }

    internal void WriteToStream(StringBuilder stringBuilder)
    {
        foreach (var item in _result)
        {
            var lst = item.Value;

            var x = MethodName + _separator;
            x += item.Key + _separator;

            x += lst.Count.ToString() + _separator;

            long nExecutionTime = 0;
            var nCountNoFirst = 0;
            long nExecutionTimeNoFirst = 0;
            for (var i = 0; i < lst.Count; i++)
            {
                if (i == 0)
                {
                    x += lst[i].DateTime.ToString("yyyy-MM-dd H:mm:ss:ff") + _separator;
                    x += lst[i].ElapsedTotalRun.ToString() + _separator;
                    x += lst[i].ElapsedSinceCp.ToString() + _separator;
                }

                nExecutionTime += lst[i].ElapsedSinceCp;
                if (i != 0)
                {
                    nCountNoFirst++;
                    nExecutionTimeNoFirst += lst[i].ElapsedSinceCp;
                }
            }

            var nAverageCp = nExecutionTime / lst.Count;
            x += nAverageCp.ToString() + _separator;

            if (nCountNoFirst == 0)
            {
                x += "n/a" + _separator;
            }
            else
            {
                var nAverageCpNoFirst = nExecutionTimeNoFirst / nCountNoFirst;

                x += nAverageCpNoFirst.ToString() + _separator;
            }

            x += nExecutionTime.ToString() + _separator;
            stringBuilder.AppendLine(x);
        }
    }

    private void WriteMessage(string text)
    {
        WriteMessage(text, new object[] { });
    }

    private void WriteMessage(string text, params object[] arg)
    {
        var dt = DateTime.Now.ToString("H:mm:ss:f");
        var str = string.Format(text, arg);
        //  Console.WriteLine("{0}:{1} {2}", dt, this.MethodName, str);
    }

    private class PerfLogItem
    {
        internal PerfLogItem(long elapsedTotalRun, long elapsedSinceCp, DateTime dateTime)
        {
            ElapsedTotalRun = elapsedTotalRun;
            ElapsedSinceCp = elapsedSinceCp;
            DateTime = dateTime;
        }

        public long ElapsedTotalRun { get; }
        public long ElapsedSinceCp { get; }
        public DateTime DateTime { get; }
    }
}