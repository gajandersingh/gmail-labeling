using ConsoleApp2;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

internal class Program
{
    private static string[] Scopes = { GmailService.Scope.GmailModify, GmailService.Scope.GmailLabels };
    private static string ApplicationName = "Gmail Priority App";

    private static void Main(string[] args)
    {
        UserCredential credential;
        using (var stream = new FileStream("credentials.json", FileMode.Open, FileAccess.Read))
        {
            string credPath = "token";
            credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                Scopes,
                "user",
                CancellationToken.None,
                new FileDataStore(credPath, true)).Result;
            Console.WriteLine($"Credential saved to: {credPath}");
        }

        var service = new GmailService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName,
        });

        var request = service.Users.Messages.List("me");
        request.MaxResults = 10;
        var labelMap = new Dictionary<string, string>
        {
            { "Reply_Must", "" },
            { "Urgent", "" },
            { "Information", "" },
            { "Low_Priority", "" },
            { "Spam_New", "" }
        };

        labelMap = LabelManager.EnsureLabels(service, labelMap);

        while (true)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (key == ConsoleKey.Q || key == ConsoleKey.Escape)
                {
                    Console.WriteLine("\nStopping…");
                    break;
                }
            }

            try
            {
                var response = request.Execute();
                if (response.Messages == null || response.Messages.Count == 0)
                {
                    Console.WriteLine("No messages found.");
                    return;
                }

                foreach (var message in response.Messages)
                {
                    var getReq = service.Users.Messages.Get("me", message.Id);
                    bool lblExist = LabelManager.GetMessageLabelIds(service, message.Id, labelMap);
                    if (!lblExist)
                    {
                        getReq.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                        Message fullMessage = getReq.Execute();

                        var headers = fullMessage.Payload.Headers;
                        string subject = headers.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "(no subject)";
                        string from = headers.FirstOrDefault(h => h.Name == "From")?.Value ?? "(unknown)";
                        string to = headers.FirstOrDefault(h => h.Name == "To")?.Value ?? "(unknown)";
                        string cc = headers.FirstOrDefault(h => h.Name == "Cc")?.Value ?? "(none)";
                        string body = "";
                        if (fullMessage.Payload?.Body?.Data != null)
                        {
                            body = fullMessage.Payload.Body.Data;
                        }
                        else if (fullMessage.Payload?.Parts != null)
                        {
                            var part = fullMessage.Payload.Parts.FirstOrDefault(p => p.MimeType == "text/plain") ?? fullMessage.Payload.Parts.FirstOrDefault();
                            if (part?.Body?.Data != null) body = part.Body.Data;
                        }

                        if (!string.IsNullOrEmpty(body))
                        {
                            body = Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/')));
                        }

                        string classification = LabelManager.ClassifyEmail(subject, from, to, cc, body);

                        if (labelMap.ContainsKey(classification))
                        {
                            var modifyRequest = new Google.Apis.Gmail.v1.Data.ModifyMessageRequest
                            {
                                AddLabelIds = new[] { labelMap[classification] }
                            };

                            service.Users.Messages.Modify(modifyRequest, "me", message.Id).Execute();
                            Console.WriteLine($"Applied label: {classification}");
                        }
                        else
                        {
                            Console.WriteLine("No matching label found.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            System.Threading.Thread.Sleep(10000); // Wait before checking for new messages
        }
    }
}