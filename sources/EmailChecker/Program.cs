using PrimS.Telnet;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmailChecker
{
    class Program
    {
        const string ROW_DELIMITER = "\r\n";
        const string CONTENT_DELIMITER = "\t";
        const string MX_INFO_CONTENT_DELIMITER = ",";
        const string MX_NAME = "mail exchanger";
        const int SMTP_PORT = 25;

        static string[] emailsToValidate = new string[0];

        static async Task MainAsync(string[] args)
        {
            var fileList = ConfigurationManager.AppSettings["EmailsList"];
            var outputFileList = ConfigurationManager.AppSettings["OutputEmailsList"];

            emailsToValidate = File.ReadAllLines(fileList);

            var allProviders = GetProvidersGrouped(emailsToValidate);

            if (allProviders != null)
            {
                var emailsChecklist = new Dictionary<string, EmailInfo>();

                foreach (var provider in allProviders)
                {
                    try
                    {
                        var mxInfo = await FindServerInfoAsync(provider.Key);
                        var allProvidersInfo = ParseMxServerResults(mxInfo);

                        var mxToUse = allProvidersInfo.OrderBy(
                            p => p.Preference
                        ).First();

                        var emailsChecked = await CheckEmailsOrquestrator(
                            32,
                            provider.Value,
                            mxToUse.Address,
                            SMTP_PORT
                        );

                        foreach (var email in emailsChecked)
                        {
                            emailsChecklist.Add(
                                email.Key,
                                email.Value
                            );
                        }
                    }
                    catch(Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }

                if (emailsChecklist != null && emailsChecklist.Count > 0)
                {
                    var outputFileName = Path.Combine(
                        outputFileList,
                        string.Format(
                            "output-{0}.csv",
                            DateTime.Now.ToString("yyyyMMddHHmmssfff")
                        )
                    );

                    if (!Directory.Exists(outputFileList))
                        Directory.CreateDirectory(outputFileList);

                    using (var writer = new StreamWriter(outputFileName))
                    {
                        writer.WriteLine("email;provider;email;");

                        foreach (var emailChecked in emailsChecklist)
                        {
                            writer.WriteLine(
                                string.Format(
                                    "{0};{1};{2};",
                                    emailChecked.Key,
                                    emailChecked.Value.ProviderExists,
                                    emailChecked.Value.EmailResolution
                                )
                            );
                        }

                        writer.Flush();
                    }
                }
            }
        }

        static void Main(string[] args)
        {
            MainAsync(args).Wait();
        }

        static string GetProviderFromEmail(string email)
        {
            var provider = string.Empty;

            if (!string.IsNullOrWhiteSpace(email) && email.Contains("@"))
            {
                var delimiter = email.LastIndexOf('@') + 1;
                provider = email.Substring(delimiter);
            }

            return provider;
        }

        static IReadOnlyDictionary<string, IEnumerable<string>> GetProvidersGrouped(
            IEnumerable<string> emails
        )
        {
            var providersDictionary = new Dictionary<string, IEnumerable<string>>(
                emails.Count()
            );

            foreach (var email in emails)
            {
                var provider = GetProviderFromEmail(email);
                if (!providersDictionary.ContainsKey(provider))
                {
                    providersDictionary.Add(
                        provider,
                        new List<string>() { email }
                    );
                }
                else
                {
                    ((List<string>)providersDictionary[provider]).Add(email);
                }
            }

            return providersDictionary;
        }

        static async Task<IEnumerable<string>> FindServerInfoAsync(string provider)
        {
            var nslookupResults = new string[0];
            using (var nslookup = new Process())
            {
                nslookup.StartInfo.FileName = "nslookup";
                nslookup.StartInfo.Arguments = string.Format("-type=mx {0}", provider);
                nslookup.StartInfo.UseShellExecute = false;
                nslookup.StartInfo.RedirectStandardOutput = true;
                nslookup.Start();

                var result = await nslookup.StandardOutput.ReadToEndAsync();
                nslookupResults = result.Split(
                    new string[] { ROW_DELIMITER },
                    StringSplitOptions.RemoveEmptyEntries
                );
            }

            return nslookupResults;
        }

        static IEnumerable<MxServerInfo> ParseMxServerResults(IEnumerable<string> results)
        {
            var tempResults = results.ToArray();

            var server = tempResults[0].Replace("Server: ", string.Empty).Trim();
            var ip = tempResults[1].Replace("Address: ", string.Empty).Trim();
            var mailExchangerList = new List<MxServerInfo>();

            var index = 2;
            while (tempResults.Length > index)
            {
                if (!tempResults[index].Contains(MX_NAME))
                {
                    index++;
                    continue;
                }

                var mxResponse = tempResults[index].Split(
                    new string[] { CONTENT_DELIMITER },
                    StringSplitOptions.RemoveEmptyEntries
                );

                if (mxResponse != null && mxResponse.Length > 0)
                {
                    var mxInfos = mxResponse[1].Split(
                        new string[] { MX_INFO_CONTENT_DELIMITER },
                        StringSplitOptions.RemoveEmptyEntries
                    );

                    if (mxInfos != null && mxInfos.Length > 0)
                    {
                        var preference = mxInfos[0]
                            .Replace("MX preference = ", string.Empty)
                            .Trim();

                        var address = mxInfos[1]
                            .Replace("mail exchanger = ", string.Empty)
                            .Trim();

                        mailExchangerList.Add(new MxServerInfo
                        {
                            Address = address,
                            Preference = int.Parse(preference)
                        });
                    }
                }

                index++;
            }

            return mailExchangerList;
        }

        static async Task<IReadOnlyDictionary<string, EmailInfo>> CheckEmailsOrquestrator(
            int maxExecutions,
            IEnumerable<string> emails,
            string mxHostAddress,
            int port
        )
        {
            var emailsChecklist = new Dictionary<string, EmailInfo>(
                emails.Count()
            );

            var mailsCount = emails.Count();
            for (int i = 0; i < mailsCount; i += maxExecutions)
            {
                var emailsToExecute = emails.Skip(i).Take(maxExecutions);
                var allTasks = emailsToExecute.Select(
                    p => CheckEmailAddresses(p, mxHostAddress, port)
                );

                //var allConvertedTasks = allTasks.ToArray();
                //this coding style lock all async thread, so, it's useless
                //Task.WaitAll(allConvertedTasks);
                Console.Write("threads: {0}\r\n", allTasks.Count());
                await Task.WhenAll(allTasks);

                foreach (var task in allTasks)
                {
                    var taskResult = task.Result;
                    if (!emailsChecklist.ContainsKey(taskResult.Email))
                    {
                        emailsChecklist.Add(taskResult.Email, taskResult);
                    }
                }
            }

            return emailsChecklist;
        }

        static async Task<EmailInfo> CheckEmailAddresses(
            string email,
            string mxHostAddress,
            int port
        )
        {
            Console.WriteLine(Thread.CurrentThread.ManagedThreadId);
            var mailInfo = new EmailInfo
            {
                ProviderExists = false,
                EmailResolution = EmailResolutionType.CantDetemine
            };

            try
            {
                using (var telnetClient = new Client(
                    mxHostAddress,
                    port,
                    CancellationToken.None
                ))
                {
                    if (mailInfo.ProviderExists = telnetClient.IsConnected)
                    {
                        mailInfo.Email = email;
                        await telnetClient.Write("HELO localhost\r\n");
                        await telnetClient.ReadAsync(new TimeSpan(0, 0, 10));

                        await telnetClient.Write("MAIL FROM:<TEST@DOMAIN.com>\r\n");
                        await telnetClient.ReadAsync(new TimeSpan(0, 0, 10));

                        await telnetClient.Write(
                            string.Format("RCPT TO:<{0}>\r\n", email)
                        );

                        var response = await telnetClient.ReadAsync(new TimeSpan(0, 1, 0));

                        if (response.StartsWith("2"))
                            mailInfo.EmailResolution = EmailResolutionType.Exist;
                        else if (response.StartsWith("5") && response.Contains("exist"))
                            mailInfo.EmailResolution = EmailResolutionType.NotExist;
                        else
                            mailInfo.EmailResolution = EmailResolutionType.CantDetemine;
                    }

                    await telnetClient.Write("QUIT");
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            return mailInfo;
        }

        class MxServerInfo
        {
            public string Address { get; set; }
            public int Preference { get; set; }
        }

        class EmailInfo
        {
            public string Email { get; set; }
            public bool ProviderExists { get; set; }
            public EmailResolutionType EmailResolution { get; set; }
        }

        enum EmailResolutionType
        {
            Exist = 0,
            NotExist = 1,
            CantDetemine = 2
        }
    }
}
