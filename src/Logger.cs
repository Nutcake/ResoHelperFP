using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ResoHelperFP;
class Logger
{
    public static void Log(object obj, bool stackTrace = false) => Logger.Log(obj?.ToString(), stackTrace);

    public static void Log(string message, bool stackTrace = false)
    {
        message += "[Log] ";
        if (stackTrace)
            message = message + "\n\n" + Environment.StackTrace;

        Console.WriteLine(message);
    }

    public static void Warning(string message, bool stackTrace = false)
    {
        message += "[Warning] ";
        if (stackTrace)
            message = message + "\n\n" + Environment.StackTrace;

        Console.WriteLine(message);
    }

    public static void Error(string message, bool stackTrace = true)
    {
        message += "[Error] ";
        if (stackTrace)
            message = message + "\n\n" + Environment.StackTrace;

        Console.WriteLine(message);
    }
}

