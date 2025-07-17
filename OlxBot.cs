using System.Text.Json;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Playwright;
using System.Text.Json.Serialization;

namespace OlxDiscordBot;

public class OlxBot
{
    private readonly DiscordSocketClient _client;
    private IBrowser? _browser;
    private readonly CancellationTokenSource _cts = new();
    private const string Token = "MTM5MDY2Njk2Mzc5NzQwOTgxMg.GeyPZQ.rHba-RC0mNeBc9gscDP3YM3mZTiy1ENl61MiyQ";
    private const int MaxOfferAgeMinutes = 10;
    private const int ScanIntervalMinutes = 2;
    private readonly HashSet<string> _sentOfferIds = new();
    private const string SentOffersFilePath = "sent_offers.txt";
    private const string FiltersFilePath = "filters.json";
    private List<FilterConfig> _filters = new();

    public OlxBot()
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages
        });

        _client.Log += LogAsync;
        _client.Ready += OnClientReady;
    }

    private Task OnClientReady()
    {
        Console.WriteLine("✅ Bot gotowy!");
        _ = Task.Run(() => CheckOffersLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task RunAsync()
    {
        LoadFilters();
        LoadSentOffers();

        await InitPlaywrightAsync();
        if (_browser == null) return;

        await _client.LoginAsync(TokenType.Bot, Token);
        await _client.StartAsync();

        await Task.Delay(Timeout.Infinite, _cts.Token);
    }

    private void LoadFilters()
    {
        try
        {
            if (!File.Exists(FiltersFilePath))
            {
                Console.WriteLine("⚠️ Brak pliku filters.json!");
                return;
            }

            var json = File.ReadAllText(FiltersFilePath);
            List<FilterConfig>? filters = JsonSerializer.Deserialize<List<FilterConfig>>(json);


            if (filters == null || filters.Count == 0)
            {
                Console.WriteLine("⚠️ Nie wczytano żadnych filtrów z filters.json!");
                return;
            }

            _filters = filters!
                .Where(f => !string.IsNullOrWhiteSpace(f.Model) && f.ChannelId != 0)
                .ToList();

            foreach (var filter in _filters)
            {
                Console.WriteLine($"✅ Wczytano filtr: model = '{filter.Model}', channelId = {filter.ChannelId}, minPrice = {filter.MinPrice}");
            }

            if (_filters.Count == 0)
            {
                Console.WriteLine("⚠️ Wszystkie filtry zostały odrzucone (brak modelu lub channelId = 0).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Błąd ładowania filters.json: {ex.Message}");
        }
    }




    private void LoadSentOffers()
    {
        if (File.Exists(SentOffersFilePath))
        {
            foreach (var line in File.ReadAllLines(SentOffersFilePath))
            {
                _sentOfferIds.Add(line.Trim());
            }
        }
    }

    private void SaveSentOffer(string offerId)
    {
        File.AppendAllLines(SentOffersFilePath, new[] { offerId });
        _sentOfferIds.Add(offerId);
    }

    private async Task CheckOffersLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Console.WriteLine("🔍 Skanuję OLX...");
                var newOffers = await GetRecentOffersAsync();

                if (newOffers.Count > 0)
                {
                    Console.WriteLine($"✅ Znaleziono {newOffers.Count} ofert");
                    await SendFilteredNotificationsAsync(newOffers);
                }
                else
                {
                    Console.WriteLine("🔄 Brak nowych ofert");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Błąd skanowania: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromMinutes(ScanIntervalMinutes), ct);
        }
    }

    private async Task<List<Offer>> GetRecentOffersAsync()
    {
        var offers = new List<Offer>();
        if (_browser == null) return offers;

        try
        {
            var context = await _browser.NewContextAsync();
            var page = await context.NewPageAsync();

            await page.GotoAsync("https://www.olx.pl/elektronika/telefony/q-iphone/?search%5Border%5D=created_at:desc", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForSelectorAsync("div[data-testid='l-card']");

            var cards = await page.QuerySelectorAllAsync("div[data-testid='l-card']");

            foreach (var element in cards)
            {
                var title = await element.EvalOnSelectorAsync<string>("a > h4", "el => el.innerText");
                var price = await element.EvalOnSelectorAsync<string>("p[data-testid='ad-price']", "el => el.innerText");
                var link = await element.EvalOnSelectorAsync<string>("a", "el => el.href");
                var dateText = await element.EvalOnSelectorAsync<string>("p[data-testid='location-date']", "el => el.innerText");

                // ✅ Parsowanie ceny wewnątrz pętli
                decimal parsedPrice = 0;
                var numericPriceMatch = Regex.Match(price ?? "", @"(\d[\d\s]*)");
                if (numericPriceMatch.Success)
                {
                    var cleaned = numericPriceMatch.Groups[1].Value.Replace(" ", "");
                    decimal.TryParse(cleaned, out parsedPrice);
                }

                if (dateText.Contains("Dzisiaj"))
                {
                    var match = Regex.Match(dateText, @"Dzisiaj o (\d{2}:\d{2})");
                    if (match.Success && DateTime.TryParse(match.Groups[1].Value, out var offerTime))
                    {
                        var offerDateTime = DateTime.Today.Add(offerTime.TimeOfDay);
                        var diff = DateTime.Now - offerDateTime;

                        if (diff.TotalMinutes <= MaxOfferAgeMinutes && diff.TotalMinutes >= 0)
                        {
                            offers.Add(new Offer
                            {
                                Title = title,
                                Price = price,
                                Link = link,
                                Date = dateText,
                                PriceValue = parsedPrice
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Błąd w GetRecentOffersAsync: {ex.Message}");
        }

        return offers;
    }



    private async Task SendFilteredNotificationsAsync(List<Offer> offers)
    {
        foreach (var offer in offers)
        {
            var offerId = GetOfferIdFromUrl(offer.Link);
            if (string.IsNullOrWhiteSpace(offerId) || _sentOfferIds.Contains(offerId))
                continue;


            foreach (var filter in _filters)
            {
                if (filter.ChannelId == 0 || string.IsNullOrWhiteSpace(filter.Model))
                {
                    Console.WriteLine($"⚠️ Pomijam filtr '{filter.Model}', ChannelId = 0.");
                    continue;
                }

                if (offer.Title.ToLower().Contains(filter.Model.ToLower()))
                {
                    if (offer.PriceValue < filter.MinPrice)
                    {
                        Console.WriteLine($"⛔ Pomijam '{offer.Title}' – cena {offer.PriceValue}zł poniżej minimum {filter.MinPrice}zł.");
                        continue;
                    }

                    await SendOfferToDiscord(offer, filter.ChannelId);
                }
            }

            SaveSentOffer(offerId);
            await Task.Delay(500); // unikanie rate-limitów
        }
    }


    private async Task SendOfferToDiscord(Offer offer, ulong channelId)
    {
        var channel = await _client.GetChannelAsync(channelId) as IMessageChannel;
        if (channel == null)
        {
            Console.WriteLine($"⚠️ Nie znaleziono kanału {channelId}");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle(offer.Title.TrimTo(256))
            .WithUrl(offer.Link)
            .WithDescription($"💰 **Cena:** {offer.Price}\n🕒 **Data i miejsce:** {offer.Date}")
            .WithColor(Color.Green)
            .WithFooter("OLX Bot")
            .WithCurrentTimestamp()
            .Build();

        await channel.SendMessageAsync(embed: embed);
        Console.WriteLine($"📤 Wysłano ofertę: {offer.Title}");
    }

    private string GetOfferIdFromUrl(string url)
    {
        var match = Regex.Match(url, @"ID(\w+)\.html", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private async Task InitPlaywrightAsync()
    {
        try
        {
            var playwright = await Playwright.CreateAsync();
            _browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { "--disable-gpu", "--disable-dev-shm-usage", "--no-sandbox" }
            });

            Console.WriteLine("🌐 Przeglądarka gotowa");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Błąd przeglądarki: {ex.Message}");
        }
    }

    private Task LogAsync(LogMessage log)
    {
        Console.WriteLine(log.ToString());
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_browser != null) await _browser.CloseAsync();
        await _client.StopAsync();
    }

    private class FilterConfig
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("channelId")]
        public ulong ChannelId { get; set; }

        [JsonPropertyName("minPrice")] 
        public int MinPrice { get; set; } = 0;
    }
}

public class Offer
{
    public string Title { get; set; } = "";
    public string Price { get; set; } = "";
    public string Link { get; set; } = "";
    public string Date { get; set; } = "";
    public decimal PriceValue { get; set; }
}

public static class StringExtensions
{
    public static string TrimTo(this string input, int maxLength)
    {
        return string.IsNullOrEmpty(input) ? input : (input.Length <= maxLength ? input : input[..maxLength]);
    }
}
