using HumbleChoice;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;


namespace HumbleChoiceUnselected
{
    public class HumbleChoiceUnselected : LibraryPlugin
    {
        const string baseUrl = "https://www.humblebundle.com/membership/";
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
            var productUrl = baseUrl + productData.RootElement.GetProperty("contentChoiceOptions").GetProperty("productUrlPath").ToString();

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
            
            if(allTitles.Count() == 0)
            {
                logger.Error("allTitles.Count() was zero, either there are no games in the product (unlikely) or we aren't looking at the correct JSON key");
            }

            var unclaimedTitles = allTitles.Where(x => !alreadyClaimedTitles.Contains(x.Name));

            return unclaimedTitles.Select((x) =>
            {
                var gameName = x.Value.GetProperty("title").ToString();

                logger.Info(gameName);

                return new GameMetadata()
                {
                    Name = gameName,
                    GameId = Id.ToString() + "/" + gameName + "/" + title,
                    Source = new MetadataNameProperty(title),
                    Links = new List<Link> { new Link("Redemption Page", productUrl + "/" + x.Name) }
                };
            });
        }

        private void RemoveExistingGames()
        {
            using (PlayniteApi.Database.BufferedUpdate())
            {
                logger.Info("Removing existing game entries created by the plugin");
                PlayniteApi.Database.Games.Where(x => x.GameId.ToString().StartsWith(Id.ToString() + "/")).ForEach(x => PlayniteApi.Database.Games.Remove(x.Id));
            }
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        { 
            var gameList = new List<GameMetadata>();

            if (settings.Settings.Cookie == String.Empty || settings.Settings.Cookie == null)
            {
                logger.Error("Cookie value not set");
                throw new Exception("Cookie value not set");
            }

            var decryptedCookie = Dpapi.Unprotect(settings.Settings.Cookie, Id.ToString(), System.Security.Cryptography.DataProtectionScope.CurrentUser);

            if(decryptedCookie == null)
            {
                logger.Error("Error decrypting cookie");
                throw new Exception("Error decrypting cookie");
            }

            var client = new WebClient();
            client.Headers[HttpRequestHeader.Cookie] = "_simpleauth_sess=" + decryptedCookie;
            logger.Info("PlaynitePlugin/HumbleChoiceUnselectedGames/" + Assembly.GetExecutingAssembly().GetName().Version);
            client.Headers[HttpRequestHeader.UserAgent] = "PlaynitePlugin/HumbleChoiceUnselectedGames/" + Assembly.GetCallingAssembly().GetName().Version;

            try
            {
                var currentMonthData = client.DownloadString("https://www.humblebundle.com/membership/home");
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
                    var requestUrl = "https://www.humblebundle.com/api/v1/subscriptions/humble_monthly/subscription_products_with_gamekeys/" + cursor;

                    logger.Info("Making request to: " + requestUrl);

                    var data = client.DownloadString(requestUrl);

                    if (data.Contains("Humble Bundle - Log In"))
                    {
                        logger.Error("Invalid session cookie");
                        throw new Exception("Invalid session cookie");
                    }

                    logger.Info("Appear to be logged in, parsing response");

                    var jsonData = JsonDocument.Parse(data);

                    logger.Info("Resonse parsed into JSON");

                    cursor = jsonData.RootElement.TryGetProperty("cursor", out var _) ? jsonData.RootElement.GetProperty("cursor").GetString() : null;

                    foreach (var product in jsonData.RootElement.GetProperty("products").EnumerateArray())
                    {

                        if (!product.TryGetProperty("title", out var title ))
                        {
                            logger.Info($"No title found so bundle appears to be an old Humble Monthly bundle, stopping here.");
                            RemoveExistingGames();
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

                            var urlPath = product.GetProperty("productUrlPath");

                            var productUrl = baseUrl + urlPath;

                            var rawProductData = ExtractProductDataFromHtml(client.DownloadString(productUrl));

                            if (rawProductData == null)
                            {
                                logger.Error("Could not extract JSON from " + rawProductData);
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
                catch(WebException webException)
                {
                    var httpResponse = (HttpWebResponse) webException.Response;
                    if(httpResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        logger.Info($"Request to {httpResponse.ResponseUri} returned 404 suggesting no further purchases to download.");
                        cursor = null;
                    }
                }catch(Exception ex)
                {
                    logger.Error("Caught a generic exception");
                    logger.Error(ex.ToString());
                    cursor = null;
                }
            }

            RemoveExistingGames();

            return gameList;  
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