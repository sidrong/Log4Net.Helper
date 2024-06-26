using System;
using System.Reflection;
using System.Threading;

using log4net;
using log4net.Core;
using log4net.Repository;

namespace Log4Net.Helper
{
    ///<summary>
    /// Used for logging
    ///</summary>
    public static class LogHelper
    {
        private static readonly object _syncRoot = new object();

        public static ILog GetLogger(string name = null, string repository = null, string configFile = null)
        {
            return GetLogger(Assembly.GetCallingAssembly(), name, repository, configFile);
        }

        public static ILog GetLogger(Assembly assembly, string name = null, string repository = null, string configFile = null)
        {
            if (string.IsNullOrEmpty(name))
            {
                Type type = typeof(LogHelper);
                name = type.FullName;
                assembly = type.Assembly;
            }
            ILog logger;
            if (!string.IsNullOrWhiteSpace(repository))
            {
                CreateRepository(repository, configFile);
                logger = LogManager.GetLogger(repository, name);
            }
            else
            {
                logger = LogManager.GetLogger(assembly, name);
            }

            if (logger != null && logger.Logger != null && logger.Logger.Repository != null && !logger.Logger.Repository.Configured)
            {
                //从Web.config加载配置
                var log4netConfigSection = System.Configuration.ConfigurationManager.GetSection("log4net");
                if (log4netConfigSection != null)
                {
                    log4net.Config.XmlConfigurator.Configure(logger.Logger.Repository);
                }
                else
                {
                    //从log4net.config加载配置（log4net.config和log4net.dll同目录）
                    string log4netConfigPath = new System.Uri(logger.GetType().Assembly.CodeBase).AbsolutePath?.ToLower()?.Replace(".dll", ".config");
                    if (System.IO.File.Exists(log4netConfigPath))
                    {
                        log4net.Config.XmlConfigurator.ConfigureAndWatch(logger.Logger.Repository, new System.IO.FileInfo(log4netConfigPath));
                    }
                    else
                    {
                        //从本程序集的资源文件中加载配置
                        var log4netConfigXmlElement = Tools.Utils.GetInternalConfig();
                        if (log4netConfigXmlElement != null)
                        {
                            log4net.Config.XmlConfigurator.Configure(logger.Logger.Repository, log4netConfigXmlElement);
                        }
                    }
                }
            }
            return logger;
        }

        public static ILoggerRepository CreateRepository(string repository, string configFile = null)
        {
            if (repository == null)
            {
                throw new ArgumentNullException(nameof(repository));
            }

            ILoggerRepository loggerRepository = null;
            if (!LoggerManager.RepositorySelector.ExistsRepository(repository))
            {
                lock (_syncRoot)
                {
                    if (!LoggerManager.RepositorySelector.ExistsRepository(repository))
                    {
                        loggerRepository = LogManager.CreateRepository(repository);
                        if (!string.IsNullOrWhiteSpace(configFile) && System.IO.File.Exists(configFile))
                        {
                            log4net.Config.XmlConfigurator.ConfigureAndWatch(loggerRepository, new System.IO.FileInfo(configFile));
                        }
                    }
                }
            }
            return loggerRepository;
        }

        private static LogMessage GetMessage(object message)
        {
            var thread = string.IsNullOrWhiteSpace(Thread.CurrentThread.Name) ? Thread.CurrentThread.ManagedThreadId.ToString() : Thread.CurrentThread.Name;
            var locationInfo = new LocationInfo(typeof(LogHelper));
            if (message is LogRouter router)
            {
                return new LogMessage(router.Message, locationInfo, thread, router.Service);
            }
            return new LogMessage(message, locationInfo, thread);
        }
        #region Error
        /// <summary>
        /// Adds an error log
        /// </summary>
        /// <param name="message"></param>
        /// <param name="exception"></param>
        /// <param name="loggerName"></param>
        /// <param name="repositoryName"></param>
        /// <param name="configFile"></param>
        public static void Error(object message, Exception exception, string loggerName = null, string repositoryName = null, string configFile = null)
        {
            var logMessage = GetMessage(message);
            var logger = GetLogger(Assembly.GetCallingAssembly(), string.IsNullOrWhiteSpace(loggerName) ? logMessage.LocationInfo?.ClassName : loggerName, repositoryName, configFile);
            if (logger == null) return;
            logger?.Error(logMessage, exception);
        }
        #endregion

        #region Warn
        /// <summary>
        /// Adds an warn log
        /// </summary>
        /// <param name="message"></param>
        /// <param name="loggerName"></param>
        /// <param name="repositoryName"></param>
        /// <param name="configFile"></param>
        public static void Warn(object message, string loggerName = null, string repositoryName = null, string configFile = null)
        {
            var logMessage = GetMessage(message);
            var logger = GetLogger(Assembly.GetCallingAssembly(), string.IsNullOrWhiteSpace(loggerName) ? logMessage.LocationInfo?.ClassName : loggerName, repositoryName, configFile);
            if (logger == null || !logger.IsWarnEnabled) return;
            logger.Warn(logMessage);
        }

        /// <summary>
        /// Adds an warn log with exception
        /// </summary>
        /// <param name="message"></param>
        /// <param name="exception"></param>
        /// <param name="loggerName"></param>
        /// <param name="repositoryName"></param>
        /// <param name="configFile"></param>
        public static void Warn(object message, Exception exception, string loggerName = null, string repositoryName = null, string configFile = null)
        {
            var logMessage = GetMessage(message);
            var logger = GetLogger(Assembly.GetCallingAssembly(), string.IsNullOrWhiteSpace(loggerName) ? logMessage.LocationInfo?.ClassName : loggerName, repositoryName, configFile);
            if (logger == null || !logger.IsWarnEnabled) return;
            logger.Warn(logMessage, exception);
        }

        #endregion

        #region Info
        /// <summary>
        /// Traces a message
        /// </summary>
        /// <param name="message"></param>
        /// <param name="loggerName"></param>
        /// <param name="repositoryName"></param>
        /// <param name="configFile"></param>
        public static void Info(object message, string loggerName = null, string repositoryName = null, string configFile = null)
        {
            var logMessage = GetMessage(message);
            var logger = GetLogger(Assembly.GetCallingAssembly(), string.IsNullOrWhiteSpace(loggerName) ? logMessage.LocationInfo?.ClassName : loggerName, repositoryName, configFile);
            if (logger == null || !logger.IsInfoEnabled) return;
            logger.Info(logMessage);
        }
        #endregion

        #region Debug
        /// <summary>
        /// Debugs if tracing is enabled.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="loggerName"></param>
        /// <param name="repositoryName"></param>
        /// <param name="configFile"></param>
        public static void Debug(object message, string loggerName = null, string repositoryName = null, string configFile = null)
        {
            var logMessage = GetMessage(message);
            var logger = GetLogger(Assembly.GetCallingAssembly(), string.IsNullOrWhiteSpace(loggerName) ? logMessage.LocationInfo?.ClassName : loggerName, repositoryName, configFile);
            if (logger == null || !logger.IsDebugEnabled) return;
            logger.Debug(logMessage);
        }
        #endregion

    }
}
