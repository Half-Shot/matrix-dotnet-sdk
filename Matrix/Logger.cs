using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Matrix
{
    public static class Logger
    {
        static ILoggerFactory factory;

        /// <summary>
        /// Needs to be externally set, one typical use case would be it's set during Startup configuration
        /// </summary>
		public static ILoggerFactory Factory {
			get 
			{
				// Stuffing in a console logger in a 2.1+ compatible way looks
				// like a lot of work if you aren't using IServiceCollection
#if NETSTANDARD2_0
				if (factory == null)
					// Call may not be present in .NET Standard 2.1
					factory = new LoggerFactory().AddConsole(LogLevel.Debug);
#endif
				// NOTE: Might not be performant always creating empty factories,
				// but want to give code opportunity to eventually log somehow
				// (i.e. you really are expected to assign Factory)
				return factory ?? new LoggerFactory(); 
			}
            set { factory = value; }
        }
    }
}