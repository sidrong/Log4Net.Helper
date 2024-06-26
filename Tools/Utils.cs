using System;
using System.Collections.Generic;
using System.Xml;
using log4net.Appender;
using log4net.Core;
using log4net.Util;

namespace Log4Net.Helper.Tools
{
    public class Utils
    {
        public static XmlElement GetInternalConfig()
        {
            try
            {
                Type logHelperType = typeof(LogHelper);
                string configPath = string.Format("{0}.log4net.config", logHelperType.Assembly.GetName().Name);
                var log4netConfigStream = logHelperType.Assembly.GetManifestResourceStream(configPath);
                if (log4netConfigStream == null)
                    return null;

                var xmldoc = new XmlDocument { XmlResolver = null };
                var xmlsettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore
                };

                using (var xmlReader = XmlReader.Create(log4netConfigStream, xmlsettings))
                {
                    xmldoc.Load(xmlReader);
                }
                XmlNodeList configNodeList = xmldoc.GetElementsByTagName("log4net");
                if (configNodeList.Count != 1 || !(configNodeList[0] is XmlElement))
                    return null;

                return configNodeList[0] as XmlElement;
            }
            catch
            {
                return null;
            }

        }

        public static bool LogToFile(object message, Level level, Exception ex = null)
        {
            if (log4net.LogManager.GetRepository() is log4net.Repository.Hierarchy.Hierarchy repository)
            {
                var appenders = repository.Root.Appenders;
                if (appenders != null)
                {
                    Type type = typeof(Utils);
                    LoggingEvent loggingEvent = new LoggingEvent(
                        type,
                        repository,
                        type.FullName,
                        level,
                        message,
                        ex
                    );
                    foreach (var item in appenders)
                    {
                        if (item is Appender.AsyncRollingFileAppender appender)
                        {
                            if (appender.IsEnabledEvent(loggingEvent))
                            {
                                appender.DoAppend(loggingEvent);
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        public static void SendEmailAlert(object message, Level level, string smtpHost, string mailFrom, string mailTo, string userName = null, string password = null, string domain = null, bool isAsync = false)
        {
            if (message == null)
                return;

            string subject = null;
            string bodyMainInfo = null;
            try
            {
                string envName = SystemInfo.HostName;
                envName = string.IsNullOrWhiteSpace(envName) ? "" : "(" + envName + ")";
                subject = @"日志服务温馨提示" + envName;
                bodyMainInfo = @"日志服务温馨提示：<br/>";
                if (level == Level.Error)
                {
                    subject = @"日志服务发生错误" + envName;
                    bodyMainInfo = @"日志服务发生错误：<br/>";
                }
                else if (level == Level.Warn)
                {
                    subject = @"日志服务发生警告" + envName;
                    bodyMainInfo = @"日志服务发生警告：<br/>";
                }
                bodyMainInfo += new LogMessage(message).ToString();

                if (string.IsNullOrWhiteSpace(smtpHost))
                    throw new Exception("No smtp host");
                if (string.IsNullOrWhiteSpace(mailFrom))
                    throw new Exception("No send from email address"); ;
                if (string.IsNullOrWhiteSpace(mailTo))
                    throw new Exception("No receive email address"); ;

                string bodyRemark = @"
                    <br/>
                    <br/>
                    (这是系统生成的电子邮件，请不要回复。)<br/><br/>
                    ";

                string cc = "";
                string bcc = "";
                string body = bodyMainInfo + bodyRemark;

                System.Net.Mail.MailMessage mail = new System.Net.Mail.MailMessage
                {
                    From = new System.Net.Mail.MailAddress(mailFrom)
                };

                if (!string.IsNullOrEmpty(mailTo))
                {
                    string[] arrMailTos = mailTo.Split(new char[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (string rec in arrMailTos)
                    {
                        if (!string.IsNullOrEmpty(rec))
                        {
                            mail.To.Add(rec);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(cc))
                {
                    string[] arrCcs = cc.Split(new char[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (string rec in arrCcs)
                    {
                        if (!string.IsNullOrEmpty(rec))
                        {
                            mail.CC.Add(rec);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(bcc))
                {
                    string[] arrbccs = bcc.Split(new char[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (string rec in arrbccs)
                    {
                        if (!string.IsNullOrEmpty(rec))
                        {
                            mail.Bcc.Add(rec);
                        }
                    }
                }


                mail.IsBodyHtml = true;
                mail.Subject = subject;
                mail.Body = (body ?? "").Replace(Environment.NewLine, "<br/>");

                System.Net.Mail.SmtpClient client = new System.Net.Mail.SmtpClient(smtpHost);
                if (!string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(password))
                {
                    if (string.IsNullOrWhiteSpace(domain))
                        client.Credentials = new System.Net.NetworkCredential(userName, password);
                    else
                        client.Credentials = new System.Net.NetworkCredential(userName, password, domain);
                }

                if (isAsync)
                {
                    client.SendAsync(mail, null);
                }
                else
                {
                    client.Send(mail);
                }
            }
            catch (Exception ex)
            {
                LogToFile(string.Format("Failed to send email alert. Email subject: {0}, Email body: {1}", subject, bodyMainInfo), Level.Error, ex);
            }
        }
    }
}
