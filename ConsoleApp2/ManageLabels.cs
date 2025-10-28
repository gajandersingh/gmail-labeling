using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ConsoleApp2
{
    /// <summary>
    /// Helper utilities for label management and email classification.
    /// </summary>
    internal static class LabelManager
    {
        /// <summary>
        /// Return an existing label ID by name, or create the label and return the new ID.
        /// </summary>
        public static string GetOrCreateLabelNotinuse(GmailService service, string labelName, string BackgroundColor, String ColorName)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (string.IsNullOrWhiteSpace(labelName)) throw new ArgumentException("labelName must be provided", nameof(labelName));

            var listReq = service.Users.Labels.List("me");
            var labels = listReq.Execute().Labels ?? Enumerable.Empty<Label>();
            var existing = labels.FirstOrDefault(l => string.Equals(l.Name, labelName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Id;

            var createLabelRequest = new Label
            {
                Name = labelName,
                LabelListVisibility = "labelShow",
                MessageListVisibility = "show",
                Color = new LabelColor
                {
                    BackgroundColor = "#FF0000",
                    TextColor = "#FFFFFF"
                }
            };

            var created = service.Users.Labels.Create(createLabelRequest, "me").Execute();
            Console.WriteLine($"Created label: {created.Name} (ID: {created.Id})");
            return created.Id;
        }

        /// <summary>
        /// Classify an email by calling Azure OpenAI (HTTP REST). Returns the model text. Expects
        /// configuration keys: OPENAIURL (base endpoint), OPENAIURLSECRET (api key),
        /// OPENAI_DEPLOYMENT (optional).
        /// </summary>
        public static string ClassifyEmail(string subject, string from, string tolist, string cclist, string body)
        {
            var secretFilePath = "secret.json";
            if (!File.Exists(secretFilePath))
            {
                Console.WriteLine("secrets.json not found! Please create one locally.");
                return "secrets.json not found!";
            }
            var json = File.ReadAllText(secretFilePath);
            var secrets = JObject.Parse(json);

            string endpoint = secrets["ApiKeys"]?[0]["OPENAIURL"]?.ToString();
            string apiKey = secrets["ApiKeys"]?[1]["OPENAIURLSECRET"]?.ToString();
            string deploymentName = ConfigurationManager.AppSettings["OPENAI_DEPLOYMENT"] ?? "gpt-4.1";
            string apiVersion = ConfigurationManager.AppSettings["OPENAI_API_VERSION"] ?? "2024-12-01-preview";

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            {
                Console.WriteLine("OpenAI endpoint or key missing in AppSettings.");
                return null;
            }

            string prompt = $@"
You are an intelligent Gmail email classifier.
Your task is to analyze each incoming or existing email and assign it to one of the predefined Gmail labels based on the rules below.
Your job is to analyze the content of an email along with the recipients (TO/CC) and assign exactly one label from the following set:
Output Format
Return only one word, which must be one of:
Reply_Must | Urgent | Information | Low_Priority | Spam_New
Do not include explanations, reasoning, or extra text.
Labeling Rules:
Reply_Must
Condition: I am in the ""To"" field.
The sender is directly requesting a reply or action from me.
Example: ""Please confirm by today"", ""Can you share the report?"".
Urgent
Condition: I am in the ""To"" field.
The sender mentions urgent, immediate, or critical action required.
I need to reply, acknowledge, or act immediately.
Example: ""Urgent! Need your response ASAP"", ""Please send acknowledgement immediately"".
Information
Condition: I am in the ""CC"" field.
The email is FYI only; I am not required to take action.
Example: ""Sharing project updates for awareness"", ""Just for your information"".
Low_Priority
Condition: I am in the ""To"" field.
The mail contains updates, work progress, or general sharing, but no action is expected from me.
Example: ""Team has completed the task"", ""Work update shared"".
Spamnew
Condition: The mail is irrelevant, advertisement, marketing, or spam.
Example: ""Buy this product now"", ""Limited time offer"".
Subject: {subject}
From: {from}
TO: {tolist}
CC: {cclist}
SELF:gajandersinghworkprofile@gmail.com  (Self email is the email address for which is label being identyfing. )
Body: {body}
";

            using (var client = new HttpClient { BaseAddress = new Uri(endpoint, UriKind.Absolute) })
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var requestBody = new
                {
                    messages = new[]
                    {
                        new { role = "system", content = "You are an assistant that classifies emails." },
                        new { role = "user", content = prompt }
                    },
                    max_tokens = 20,
                    temperature = 0
                };

                string jsonBody = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                string url = $"/openai/deployments/{deploymentName}/chat/completions?api-version={apiVersion}";

                var response = client.PostAsync(url, content).Result;
                response.EnsureSuccessStatusCode();

                string result = response.Content.ReadAsStringAsync().Result;
                using (var doc = JsonDocument.Parse(result))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var message = choices[0].GetProperty("message");
                        var contentText = message.GetProperty("content").GetString();
                        contentText = contentText?.Trim().Trim('"');
                        Console.WriteLine($"AI Classification: {contentText}");
                        return contentText;
                    }
                }
            }

            return null;
        }

        public static bool GetMessageLabelIds(GmailService svc, string messageId, Dictionary<string, string> labelMap)
        {
            bool foundLabel = false;
            var getReq = svc.Users.Messages.Get("me", messageId);
            getReq.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata; // labelIds are returned
            var msg = getReq.Execute();
            List<string> existinglst = msg.LabelIds.ToList<string>();
            for (int i = 0; i < existinglst.Count; i++)
            {
                if (labelMap.Values.Any(v => v.Contains(existinglst[i])))
                {
                    foundLabel = true;
                    break;
                }
            }
            return foundLabel;
        }

        public static Dictionary<string, string> EnsureLabels(GmailService svc, Dictionary<string, string> labelMap)
        {
            var list = svc.Users.Labels.List("me").Execute();
            var existing = list.Labels.ToDictionary(l => l.Name, l => l.Id, StringComparer.OrdinalIgnoreCase);

            var updatedMap = new Dictionary<string, string>();

            foreach (var kvp in labelMap)
            {
                // If ID is valid, keep it
                if (list.Labels.Any(l => l.Id == kvp.Value))
                {
                    updatedMap[kvp.Key] = kvp.Value;
                    continue;
                }

                // Otherwise, check if label by name exists
                if (existing.ContainsKey(kvp.Key))
                {
                    updatedMap[kvp.Key] = existing[kvp.Key];
                    Console.WriteLine($"Updated {kvp.Key} to existing ID: {existing[kvp.Key]}");
                }
                else
                {
                    // Create new label
                    var newLabel = new Label
                    {
                        Name = kvp.Key,
                        LabelListVisibility = "labelShow",
                        MessageListVisibility = "show"
                    };
                    var created = svc.Users.Labels.Create(newLabel, "me").Execute();
                    updatedMap[kvp.Key] = created.Id;
                    Console.WriteLine($"Created new label {kvp.Key} with ID: {created.Id}");
                }
            }

            return updatedMap;
        }
    }
}