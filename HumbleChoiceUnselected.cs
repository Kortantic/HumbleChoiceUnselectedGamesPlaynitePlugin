using HumbleChoice;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;


namespace HumbleChoiceUnselected
{
    public class HumbleChoiceUnselected : LibraryPlugin
    {
        const string baseUrl = "https://www.humblebundle.com";
        const string choiceUrl = "https://www.humblebundle.com/membership";
        const string apiUrl = "https://www.humblebundle.com/api/v1";
        string ExtractProductDataFromHtml(string html, string needle = "<script id=\"webpack-monthly-product-data\" type=\"application/json\">")
        {
            var needleExists = html.IndexOf(needle) > -1;

            if (!needleExists)
            {
                return null;
            }

            var searchStart = html.IndexOf(needle) + needle.Length;
            var endTagLocation = html.IndexOf("</script>", searchStart);

            if (endTagLocation > -1)
            {
                return html.Substring(searchStart, endTagLocation - searchStart);
            }

            return null;
        }

        bool ChoicesAvailable(JsonDocument productData)
        {
            var initialKeyExists = productData.RootElement.GetProperty("contentChoiceOptions").GetProperty("contentChoiceData").TryGetProperty("initial", out var initial);

            if (!initialKeyExists)
            {
                return true;
            }

            initial.TryGetProperty("total_choices", out var choiceLimit);

            var contentChoiceOptions = productData.RootElement.GetProperty("contentChoiceOptions");

            if (!contentChoiceOptions.TryGetProperty("contentChoicesMade", out var _))
            {
                return true;
            }

            var numChoicesMade = contentChoiceOptions.GetProperty("contentChoicesMade").GetProperty("initial").GetProperty("choices_made").GetArrayLength();

            return choiceLimit.GetInt32() > numChoicesMade;
        }

        private static readonly ILogger logger = LogManager.GetLogger();

        public ILogger GetLogger()
        {
            return logger;
        }

        private HumbleChoiceUnselectedSettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("64ca9178-daac-44e7-a80b-0a33efaa9e6e");

        // Change to something more appropriate
        public override string Name => "Humble Choice (Unselected)";

        // Implementing Client adds ability to open it via special menu in playnite.
        public override LibraryClient Client { get; } = new HumbleChoiceUnselectedClient();

        public override string LibraryIcon => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"icon.png");

        public HumbleChoiceUnselected(IPlayniteAPI api) : base(api)
        {
            settings = new HumbleChoiceUnselectedSettingsViewModel(this);
            Properties = new LibraryPluginProperties
            {
                HasSettings = true
            };
        }

        private IEnumerable<GameMetadata> ProcessProduct(string rawProductData)
        {
            var productData = JsonDocument.Parse(rawProductData);

            var title = productData.RootElement.GetProperty("contentChoiceOptions").GetProperty("title").ToString();
            var productUrl = $"{choiceUrl}/{productData.RootElement.GetProperty("contentChoiceOptions").GetProperty("productUrlPath").ToString()}";

            if (!productData.RootElement.GetProperty("contentChoiceOptions").TryGetProperty("gamekey", out var _))
            {
                logger.Info($"No gamekey found so skipping.");
                return new List<GameMetadata>();
            }

            if (!ChoicesAvailable(productData))
            {
                logger.Info($"No choices available for {title}");
                return new List<GameMetadata>();
            }

            var alreadyClaimedTitles = new String[] { };

            if (productData.RootElement.GetProperty("contentChoiceOptions").TryGetProperty("contentChoicesMade", out var choicesMade))
            {
                alreadyClaimedTitles = choicesMade.GetProperty("initial").GetProperty("choices_made").EnumerateArray().Select(x => x.ToString()).ToArray();
            }

            var contentChoiceData = productData.RootElement.GetProperty("contentChoiceOptions").GetProperty("contentChoiceData");

            JsonElement.ObjectEnumerator allTitles = new JsonElement.ObjectEnumerator();

            if (contentChoiceData.TryGetProperty("game_data", out var gameData))
            {
                allTitles = gameData.EnumerateObject();
            }
            else
            {
                foreach (var possibleKey in new string[] { "initial", "initial-get-all-games" })
                {
                    if (contentChoiceData.TryGetProperty(possibleKey, out var key))
                    {
                        allTitles = key.GetProperty("content_choices").EnumerateObject();
                    }
                }
            }

            logger.Info($"Found {allTitles.Count()} games in {title}");

            if (allTitles.Count() == 0)
            {
                logger.Error("allTitles.Count() was zero, either there are no games in the product (unlikely) or we aren't looking at the correct JSON key");
            }

            var unclaimedTitles = allTitles.Where(x => !alreadyClaimedTitles.Contains(x.Name));
            var unclaimedChoiceMetaDatas = unclaimedTitles.Select((x) =>
            {
                var gameName = x.Value.GetProperty("title").ToString();

                logger.Info(gameName);

                return new GameMetadata()
                {
                    Name = gameName,
                    GameId = MakeID(gameName, title),
                    Source = new MetadataNameProperty(title),
                    Links = new List<Link> { new Link("Redemption Page", $"{productUrl}/{x.Name}") }
                };
            });

            return unclaimedChoiceMetaDatas;
        }

        private void RemoveClaimedAndExpiredGames(List<GameMetadata> fromHumble)
        {
            logger.Info($"Removing games that have been claimed or whose keys have expired");
            var library = PlayniteApi.Database.Games;
            var HcUnselectedLibrary = library.Where(x => x.GameId.StartsWith($"{Id.ToString()}/"));

            var idsFromHumble = new HashSet<string>(fromHumble.Select(g => g.GameId), StringComparer.OrdinalIgnoreCase);
            var toRemove = HcUnselectedLibrary.Where(g => !idsFromHumble.Contains(g.GameId)).ToList();
            using (PlayniteApi.Database.BufferedUpdate())
            {
                toRemove.ForEach(g => PlayniteApi.Database.Games.Remove(g.Id));
            }

            if (toRemove.Count > 0)
            {
                var notification = new NotificationMessage(
                        "Games Claimed or Expired",
                        $"The following Humble Choice games were either claimed or their keys expired: {String.Join(", ", toRemove.Select(g => g.Name))}",
                        NotificationType.Info
                        );
                PlayniteApi.Notifications.Add(notification);
            }

            var idsInLibrary = new HashSet<string>(HcUnselectedLibrary.Select(g => g.GameId));
            var addedIds = idsFromHumble.Where(g => !idsInLibrary.Contains(g));
            var addedGames = fromHumble.Where(g => addedIds.Contains(g.GameId)).ToList();
            if (addedGames.Count > 0 && addedGames.Count < idsFromHumble.Count)
            {
                var notification = new NotificationMessage(
                        "Humble Choice Games Added",
                        $"The following games are now available in Humble Choice: {String.Join(", ", addedGames.Select(g => g.Name))}",
                        NotificationType.Info
                        );
                PlayniteApi.Notifications.Add(notification);
            }
        }


        private string MakeID(string name, string source)
        {
            return $"{Id.ToString()}/{name}/{source}";
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            WebClient client = GetHumbleClient();
            var games = GetChoiceGames(client).ToList();
            if (settings.Settings.ImportEntitlements)
            {
                games.AddRange(GetEntitlements(client));
            }
            return games;
        }

        private IEnumerable<GameMetadata> GetChoiceGames(WebClient client)
        {
            var gameList = new List<GameMetadata>();
            // TODO: Remove this, this is here to speed up testing of entitlements
            var library = PlayniteApi.Database.Games;
            var HcUnselectedLibrary = library.Where(x => x.GameId.StartsWith($"{Id.ToString()}/"));
            return HcUnselectedLibrary.Select(g => new GameMetadata() { Name = g.Name, GameId = g.GameId, Source = new MetadataNameProperty(g.Source.Name), Links = g.Links.ToList() });

            try
            {
                var currentMonthData = client.DownloadString($"{choiceUrl}/home");
                var currentMonthDataJson = ExtractProductDataFromHtml(currentMonthData, "<script id=\"webpack-subscriber-hub-data\" type=\"application/json\">");

                if (currentMonthDataJson == null)
                {
                    logger.Error("Couldn't extract JSON data for current month");
                }
                else
                {
                    gameList.AddRange(ProcessProduct(currentMonthDataJson));
                }
            }
            catch (Exception e)
            {
                logger.Error("There was an error when fetching the data for the current month");
                logger.Error(e.ToString());
            }

            var cursor = "";

            while (cursor != null)
            {
                try
                {
                    var requestUrl = $"{apiUrl}/subscriptions/humble_monthly/subscription_products_with_gamekeys/{cursor}";

                    JsonDocument jsonData = AuthenticatedApiRequest(client, requestUrl);

                    cursor = jsonData.RootElement.TryGetProperty("cursor", out var _) ? jsonData.RootElement.GetProperty("cursor").GetString() : null;

                    foreach (var product in jsonData.RootElement.GetProperty("products").EnumerateArray())
                    {

                        if (!product.TryGetProperty("title", out var title))
                        {
                            logger.Info($"No title found so bundle appears to be an old Humble Monthly bundle, stopping here.");
                            RemoveClaimedAndExpiredGames(gameList);
                            return gameList;
                        }

                        try
                        {
                            logger.Info($"Found {title}");

                            if (!product.TryGetProperty("gamekey", out var _))
                            {
                                logger.Info($"No gamekey found so skipping.");
                                continue;
                            }

                            var urlPath = product.GetProperty("productUrlPath").ToString();

                            var productUrl = $"{choiceUrl}/{urlPath}";

                            var rawProductData = ExtractProductDataFromHtml(client.DownloadString(productUrl));

                            if (rawProductData == null)
                            {
                                logger.Error($"Could not extract JSON from {rawProductData}");
                                continue;
                            }


                            gameList.AddRange(ProcessProduct(rawProductData));
                        }
                        catch (Exception ex)
                        {
                            {
                                logger.Error($"Failed to extract games from {title}");
                                logger.Error(ex.ToString());
                            }
                        }
                    }
                }
                catch (WebException webException)
                {
                    var httpResponse = (HttpWebResponse)webException.Response;
                    if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        logger.Info($"Request to {httpResponse.ResponseUri} returned 404 suggesting no further purchases to download.");
                        cursor = null;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error("Caught a generic exception");
                    logger.Error(ex.ToString());
                    cursor = null;
                }
            }

            RemoveClaimedAndExpiredGames(gameList);

            return gameList;
        }

        private static JsonDocument AuthenticatedApiRequest(WebClient client, string apiRequestUrl)
        {
            logger.Info($"Making request to: {apiRequestUrl}");

            var data = client.DownloadString(apiRequestUrl);

            if (data.Contains("Humble Bundle - Log In"))
            {
                logger.Error("Invalid session cookie");
                throw new Exception("Invalid session cookie");
            }

            logger.Info("Appear to be logged in, parsing response");

            var jsonData = JsonDocument.Parse(data);

            logger.Info("Resonse parsed into JSON");
            return jsonData;
        }

        private WebClient GetHumbleClient()
        {
            if (settings.Settings.Cookie == String.Empty || settings.Settings.Cookie == null)
            {
                logger.Error("Cookie value not set");
                throw new Exception("Cookie value not set");
            }

            var decryptedCookie = Dpapi.Unprotect(settings.Settings.Cookie, Id.ToString(), System.Security.Cryptography.DataProtectionScope.CurrentUser);

            if (decryptedCookie == null)
            {
                logger.Error("Error decrypting cookie");
                throw new Exception("Error decrypting cookie");
            }

            var client = new WebClient();
            client.Headers[HttpRequestHeader.Cookie] = $"_simpleauth_sess={decryptedCookie}";
            logger.Info($"PlaynitePlugin/HumbleChoiceUnselectedGames/{Assembly.GetExecutingAssembly().GetName().Version}");
            client.Headers[HttpRequestHeader.UserAgent] = $"PlaynitePlugin/HumbleChoiceUnselectedGames/{Assembly.GetCallingAssembly().GetName().Version}";
            return client;
        }

        private IEnumerable<GameMetadata> GetEntitlements(WebClient client)
        {
            try
            {
                var requestUrl = $"{baseUrl}/home/keys";
                var entitlementsPageHtml = client.DownloadString(requestUrl);
                var userHomeJsonData = ExtractProductDataFromHtml(entitlementsPageHtml, "<script id=\"user-home-json-data\" type=\"application/json\">");
                var userHomeObject = JsonNode.Parse(userHomeJsonData).AsObject();
                if (userHomeObject.TryGetPropertyValue("gamekeys", out var keysNode))
                {
                    var keys = keysNode.AsArray().Select(k => k.GetValue<string>());
                    while (keys.Any())
                    {
                        var chunk = keys.Take(40);

                        logger.Info("Processing chunk:");
                        foreach (var key in chunk)
                        {
                            Debug.WriteLine(key);
                        }
                        keys = keys.Skip(40);
                    }
                    logger.Info("Finished processing keys");
                }
            }
            catch (Exception e)
            {
                logger.Error("Couldn't fetch entitlements");
                logger.Error(e.ToString());
            }
            return new List<GameMetadata>();
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new HumbleChoiceUnselectedSettingsView();
        }
    }
}
