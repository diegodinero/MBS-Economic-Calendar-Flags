using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace MBS_Economic_Calendar_Flags
{
    /// <summary>
    /// An example of blank indicator. Add your code, compile it and use on the charts in the assigned trading terminal.
    /// Information about API you can find here: http://api.quantower.com
    /// Code samples: https://github.com/Quantower/Examples
    /// </summary>
	public class MBS_Economic_Calendar_Flags : Indicator
    {

        //–– Settings fields
        private int dateMode = 1;  // 1 = current chart date, 2 = custom range
        private DateTime customStartDate = DateTime.UtcNow.Date;
        private DateTime customEndDate = DateTime.UtcNow.Date;

        private bool highImpact = true;
        private bool mediumImpact = true;
        private bool lowImpact = true;
        private bool nonEconomicNews = true;

        private int currencyMode = 1;  // 1 = all, 2 = select
        private bool audSelected;
        private bool cadSelected;
        private bool chfSelected;
        private bool cnySelected;
        private bool eurSelected;
        private bool gbpSelected;
        private bool jpySelected;
        private bool nzdSelected;
        private bool usdSelected;

        private bool showNewsText = true;
        private bool showVerticalLines = true;
        private bool showPastEvents = false;


        //–– Runtime state
        private List<ForexEvent>? allEvents;
        private List<ForexEvent>? forexEvents;
        private Exception? fetchError;
        private Font font = new Font("Consolas", 10f);
        private readonly object lockObject = new object();
        private int newsPositionX = 500;
        private int newsPositionY = 10;
        private static readonly TimeZoneInfo EasternTimeZone = ResolveEasternTimeZone();
        private static readonly SemaphoreSlim CacheSemaphore = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<string, Image?> FlagImageCache = new ConcurrentDictionary<string, Image?>(StringComparer.OrdinalIgnoreCase);
        private static readonly IReadOnlyDictionary<string, string> CurrencyFlagFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AUD"] = "australia.png",
            ["CAD"] = "canada.png",
            ["CHF"] = "switzerland.png",
            ["CNY"] = "china.png",
            ["EUR"] = "euro_zone.png",
            ["GBP"] = "united_kingdom.png",
            ["JPY"] = "japan.png",
            ["NZD"] = "new_zealand.png",
            ["USD"] = "united_states.png"
        };
        private static List<ForexEvent>? cachedEvents;
        private static DateTime cacheFetchedAtUtc = DateTime.MinValue;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
        private const int FlagMarkerSize = 22;
        private const int FlagInnerPadding = 2;
        private const int FlagStackSpacing = 4;
        private const int FlagBottomMargin = 28;

        //–– XML feed URL
        private const string XmlFeedUrl = "https://nfs.faireconomy.media/ff_calendar_thisweek.xml";

        /// <summary>
        /// Indicator's constructor. Contains general information: name, description, LineSeries etc. 
        /// </summary>
        public MBS_Economic_Calendar_Flags()
            : base()
        {
            // Defines indicator's name and description.
            Name = "Economic Events Flags";
            Description = "Display Economic Events Flags";

            // By default indicator will be applied on main window of the chart
            SeparateWindow = false;
        }

        /// <summary>
        /// This function will be called after creating an indicator as well as after its input params reset or chart (symbol or timeframe) updates.
        /// </summary>
        protected override void OnInit()
        {
            base.OnInit();
            foreach (var fam in new[] { "Droid Sans Mono", "DejaVu Sans Mono", "Consolas", "Verdana" })
            {
                var test = new Font(fam, 10f);
                font = test;
                if (font.Name == fam)
                    break;
            }
            _ = FetchDataOnce();
        }

        private async Task FetchDataOnce()
        {
            try
            {
                allEvents = await GetCachedEventsAsync();
                Debug.WriteLine($"[EconomicEventsIndicator] Fetched {allEvents.Count} events");
                fetchError = null;
            }
            catch (Exception ex)
            {
                fetchError = ex;
                Debug.WriteLine($"[EconomicEventsIndicator] Fetch error: {ex}");
            }

            ApplyFilters();
        }

        private void ApplyFilters()
        {
            lock (lockObject)
            {
                if (allEvents == null)
                {
                    forexEvents = null;
                }
                else
                {
                    List<ForexEvent> temp;
                    if (dateMode == 1)
                    {
                        var chartDate = Symbol?.LastDateTime.Date ?? DateTime.Today;
                        temp = allEvents.Where(e => e.Date.Date == chartDate).ToList();
                    }
                    else
                    {
                        temp = allEvents
                            .Where(e => e.Date.Date >= customStartDate.Date
                                     && e.Date.Date <= customEndDate.Date)
                            .ToList();

                        if (!showPastEvents)
                        {
                            var referenceDateTimeUtc = GetReferenceDateTimeUtc();
                            temp = temp
                                .Where(e => !TryGetEventDateTimeUtc(e, out var eventDateTimeUtc)
                                        || eventDateTimeUtc >= referenceDateTimeUtc)
                                .ToList();
                        }
                    }
                    forexEvents = temp.Where(ShouldIncludeEvent).ToList();
                }
            }
            this.Refresh();
        }

        private bool ShouldIncludeEvent(ForexEvent e)
        {
            if (currencyMode == 2)
            {
                var allowed = new HashSet<string>();
                if (audSelected) allowed.Add("AUD");
                if (cadSelected) allowed.Add("CAD");
                if (chfSelected) allowed.Add("CHF");
                if (cnySelected) allowed.Add("CNY");
                if (eurSelected) allowed.Add("EUR");
                if (gbpSelected) allowed.Add("GBP");
                if (jpySelected) allowed.Add("JPY");
                if (nzdSelected) allowed.Add("NZD");
                if (usdSelected) allowed.Add("USD");
                if (!allowed.Contains(e.Currency))
                    return false;
            }
            return (e.Impact.Equals("High", StringComparison.OrdinalIgnoreCase) ? highImpact : true)
                && (e.Impact.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? mediumImpact : true)
                && (e.Impact.Equals("Low", StringComparison.OrdinalIgnoreCase) ? lowImpact : true)
                && (e.Impact.Equals("Holiday", StringComparison.OrdinalIgnoreCase) ? nonEconomicNews : true);
        }

        protected override void OnSettingsUpdated()
        {
            base.OnSettingsUpdated();
            ApplyFilters();
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            if (CurrentChart == null || Symbol == null)
                return;

            var g = args.Graphics;
            var rect = CurrentChart.Windows[args.WindowIndex].ClientRectangle;
            g.SetClip(rect);

            int x = rect.Left + newsPositionX;
            int y = rect.Top + newsPositionY;

            // ✅ Only show header + status messages if showNewsText = true
            if (showNewsText)
            {
                var headerFont = new Font(font.FontFamily, 12f, FontStyle.Bold);
                string header;
                if (dateMode == 1)
                {
                    var chartDate = Symbol.LastDateTime.Date;
                    header = $"Events for {chartDate:MM/dd/yyyy}";
                }
                else
                {
                    header = $"{DateTime.Now:MM/dd/yy} News via Forex Factory";
                }

                g.DrawString(header, headerFont, Brushes.Cyan, x + 2, y + 2);
                y += headerFont.Height + 6;

                if (fetchError != null)
                {
                    //g.DrawString($"Error: {fetchError.Message}", font, Brushes.Red, x + 2, y + 2);
                    y += font.Height + 6;
                }
                else if (allEvents == null)
                {
                    g.DrawString("Downloading...", font, Brushes.Yellow, x + 2, y + 2);
                    y += font.Height + 6;
                }
                else if (forexEvents == null)
                {
                    g.DrawString("Filtering...", font, Brushes.Yellow, x + 2, y + 2);
                    y += font.Height + 6;
                }
                else
                {
                    g.DrawString($"Showing {forexEvents.Count} events", font, Brushes.Yellow, x + 2, y + 2);
                    y += font.Height + 6;
                }
            }

            // ✅ Event rendering section
            if (forexEvents != null)
            {
                var conv = CurrentChart
                    .Windows[args.WindowIndex]
                    .CoordinatesConverter;

                foreach (var ev in forexEvents.OrderBy(ParseEventDateTimeForSorting))
                {
                    // Pick line color
                    Pen linePen =
                        ev.Impact.Equals("High", StringComparison.OrdinalIgnoreCase) ? Pens.Red :
                        ev.Impact.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? Pens.Orange :
                        ev.Impact.Equals("Low", StringComparison.OrdinalIgnoreCase) ? Pens.Green :
                        Pens.White;

                    // Convert event time
                    if (TryGetEventDateTimeUtc(ev, out var eventDateTimeUtc))
                    {
                        if (showVerticalLines)
                        {
                            float xCoord = (float)conv.GetChartX(eventDateTimeUtc);
                            g.DrawLine(linePen, xCoord, rect.Top, xCoord, rect.Bottom);
                        }
                    }

                    // ✅ Only show text if enabled
                    if (showNewsText)
                    {
                        Brush simpactBrush =
                            ev.Impact.Equals("High", StringComparison.OrdinalIgnoreCase) ? Brushes.Red :
                            ev.Impact.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? Brushes.Orange :
                            ev.Impact.Equals("Low", StringComparison.OrdinalIgnoreCase) ? Brushes.Green :
                            Brushes.White;

                        g.DrawString($"{ev.Time} {ev.Currency} ", font, Brushes.White, x + 2, y + 2);
                        g.DrawString(ev.Event, font, simpactBrush, x + 100, y + 2);
                        y += font.Height + 6;
                    }
                }

                DrawEventFlags(g, rect, eventTimeUtc => (float)conv.GetChartX(eventTimeUtc));
            }
        }

        private void DrawEventFlags(Graphics graphics, Rectangle rect, Func<DateTime, float> getChartX)
        {
            if (forexEvents == null)
                return;

            var groupedEvents = new SortedDictionary<DateTime, List<ForexEvent>>();
            foreach (var ev in forexEvents)
            {
                if (!TryGetEventDateTimeUtc(ev, out var eventDateTimeUtc))
                    continue;

                if (!groupedEvents.TryGetValue(eventDateTimeUtc, out var eventsAtTime))
                {
                    eventsAtTime = new List<ForexEvent>();
                    groupedEvents[eventDateTimeUtc] = eventsAtTime;
                }

                eventsAtTime.Add(ev);
            }

            foreach (var group in groupedEvents)
            {
                float xCoord = getChartX(group.Key);
                if (xCoord < rect.Left - FlagMarkerSize || xCoord > rect.Right + FlagMarkerSize)
                    continue;

                int stackIndex = 0;
                foreach (var ev in group.Value)
                {
                    var flagImage = GetFlagImage(ev.Currency);
                    if (flagImage == null)
                        continue;

                    int drawX = (int)Math.Round(xCoord - (FlagMarkerSize / 2f));
                    int drawY = rect.Bottom - FlagBottomMargin - FlagMarkerSize - (stackIndex * (FlagMarkerSize + FlagStackSpacing));
                    if (drawY < rect.Top)
                        break;

                    DrawFlagMarker(graphics, flagImage, new Rectangle(drawX, drawY, FlagMarkerSize, FlagMarkerSize));
                    stackIndex++;
                }
            }
        }

        private static void DrawFlagMarker(Graphics graphics, Image flagImage, Rectangle bounds)
        {
            using var outlinePen = new Pen(Color.Yellow, 2f);
            using var backgroundBrush = new SolidBrush(Color.FromArgb(20, 20, 20));
            using var clipPath = new GraphicsPath();
            clipPath.AddEllipse(bounds);

            var state = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillEllipse(backgroundBrush, bounds);
                graphics.SetClip(clipPath);
                graphics.DrawImage(flagImage, Rectangle.Inflate(bounds, -FlagInnerPadding, -FlagInnerPadding));
            }
            finally
            {
                graphics.Restore(state);
            }

            graphics.DrawEllipse(outlinePen, bounds);
        }


        private static DateTime ParseEventDateTimeForSorting(ForexEvent forexEvent)
        {
            if (TryGetEventDateTimeUtc(forexEvent, out var eventDateTimeUtc))
                return eventDateTimeUtc;

            return forexEvent.Date.Date;
        }

        public override void Dispose()
        {
            font.Dispose();
            base.Dispose();
        }

        public override IList<SettingItem> Settings
        {
            get
            {
                var settings = base.Settings;

                var siCurrent = new SelectItem("Current Chart Date", 1);
                var siCustom = new SelectItem("Custom Date", 2);

                settings.Add(new SettingItemSelectorLocalized(
                    "dateMode",
                    new SelectItem("dateMode", dateMode),
                    new List<SelectItem> { siCurrent, siCustom },
                    0
                )
                { Text = "Select Date:" });

                settings.Add(new SettingItemDateTime("customStartDate", customStartDate)
                {
                    Text = "From Date",
                    Relation = new SettingItemRelationVisibility("dateMode", new object[] { siCustom })
                });

                settings.Add(new SettingItemDateTime("customEndDate", customEndDate)
                {
                    Text = "To Date",
                    Relation = new SettingItemRelationVisibility("dateMode", new object[] { siCustom })
                });

                settings.Add(new SettingItemBoolean("highImpact", highImpact) { Text = "High Impact" });
                settings.Add(new SettingItemBoolean("mediumImpact", mediumImpact) { Text = "Medium Impact" });
                settings.Add(new SettingItemBoolean("lowImpact", lowImpact) { Text = "Low Impact" });
                settings.Add(new SettingItemBoolean("nonEconomicNews", nonEconomicNews) { Text = "Non-Economic" });

                var siAllCurrency = new SelectItem("All", 1);
                var siSelectCurrency = new SelectItem("Select Currency", 2);
                settings.Add(new SettingItemSelectorLocalized(
                    "currencyMode",
                    new SelectItem("currencyMode", currencyMode),
                    new List<SelectItem> { siAllCurrency, siSelectCurrency },
                    0
                )
                { Text = "Currency:" });

                void addCurr(string prop, bool val)
                {
                    settings.Add(new SettingItemBoolean(prop, val)
                    {
                        Text = prop.Replace("Selected", ""),
                        Relation = new SettingItemRelationVisibility("currencyMode", new object[] { siSelectCurrency })
                    });
                }

                addCurr("usdSelected", usdSelected);
                addCurr("eurSelected", eurSelected);
                addCurr("audSelected", audSelected);
                addCurr("cadSelected", cadSelected);
                addCurr("chfSelected", chfSelected);
                addCurr("cnySelected", cnySelected);
                addCurr("gbpSelected", gbpSelected);
                addCurr("jpySelected", jpySelected);
                addCurr("nzdSelected", nzdSelected);

                settings.Add(new SettingItemInteger("newsPositionX", newsPositionX) { Text = "Move Left/Right (-/+)" });
                settings.Add(new SettingItemInteger("newsPositionY", newsPositionY) { Text = "Move Up/Down (-/+)" });

                settings.Add(new SettingItemBoolean("showNewsText", showNewsText) { Text = "Show News Text" });
                settings.Add(new SettingItemBoolean("showVerticalLines", showVerticalLines) { Text = "Show Vertical Lines" });
                settings.Add(new SettingItemBoolean("showPastEvents", showPastEvents)
                {
                    Text = "Show Past Events",
                    Relation = new SettingItemRelationVisibility("dateMode", new object[] { siCustom })
                });


                return settings;
            }
            set
            {
                if (SettingItemExtensions.TryGetValue<int>(value, "dateMode", out var dm)) dateMode = dm;
                if (SettingItemExtensions.TryGetValue<DateTime>(value, "customStartDate", out var cs)) customStartDate = cs;
                if (SettingItemExtensions.TryGetValue<DateTime>(value, "customEndDate", out var ce)) customEndDate = ce;

                if (SettingItemExtensions.TryGetValue<bool>(value, "highImpact", out var hi)) highImpact = hi;
                if (SettingItemExtensions.TryGetValue<bool>(value, "mediumImpact", out var mi)) mediumImpact = mi;
                if (SettingItemExtensions.TryGetValue<bool>(value, "lowImpact", out var lo)) lowImpact = lo;
                if (SettingItemExtensions.TryGetValue<bool>(value, "nonEconomicNews", out var ne)) nonEconomicNews = ne;

                if (SettingItemExtensions.TryGetValue<int>(value, "currencyMode", out var cm)) currencyMode = cm;

                if (SettingItemExtensions.TryGetValue<bool>(value, "audSelected", out var a)) audSelected = a;
                if (SettingItemExtensions.TryGetValue<bool>(value, "cadSelected", out var c)) cadSelected = c;
                if (SettingItemExtensions.TryGetValue<bool>(value, "chfSelected", out var h)) chfSelected = h;
                if (SettingItemExtensions.TryGetValue<bool>(value, "cnySelected", out var y)) cnySelected = y;
                if (SettingItemExtensions.TryGetValue<bool>(value, "eurSelected", out var e)) eurSelected = e;
                if (SettingItemExtensions.TryGetValue<bool>(value, "gbpSelected", out var g)) gbpSelected = g;
                if (SettingItemExtensions.TryGetValue<bool>(value, "jpySelected", out var j)) jpySelected = j;
                if (SettingItemExtensions.TryGetValue<bool>(value, "nzdSelected", out var n)) nzdSelected = n;
                if (SettingItemExtensions.TryGetValue<bool>(value, "usdSelected", out var u)) usdSelected = u;

                if (SettingItemExtensions.TryGetValue<int>(value, "newsPositionX", out var nx)) newsPositionX = nx;
                if (SettingItemExtensions.TryGetValue<int>(value, "newsPositionY", out var ny)) newsPositionY = ny;

                if (SettingItemExtensions.TryGetValue<bool>(value, "showNewsText", out var snt)) showNewsText = snt;
                if (SettingItemExtensions.TryGetValue<bool>(value, "showVerticalLines", out var svl)) showVerticalLines = svl;
                if (SettingItemExtensions.TryGetValue<bool>(value, "showPastEvents", out var spe)) showPastEvents = spe;


                ApplyFilters();
            }
        }

        private static TimeZoneInfo ResolveEasternTimeZone()
        {
            foreach (var id in new[] { "Eastern Standard Time", "America/New_York" })
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (TimeZoneNotFoundException)
                {
                }
                catch (InvalidTimeZoneException)
                {
                }
            }

            return TimeZoneInfo.Utc;
        }

        private static bool TryGetFreshCache(out List<ForexEvent>? events)
        {
            events = null;
            if (cachedEvents == null)
                return false;

            if (DateTime.UtcNow - cacheFetchedAtUtc > CacheLifetime)
                return false;

            events = CloneEvents(cachedEvents);
            return true;
        }

        private static async Task<List<ForexEvent>> GetCachedEventsAsync()
        {
            if (TryGetFreshCache(out var freshEvents) && freshEvents != null)
                return freshEvents;

            await CacheSemaphore.WaitAsync();
            try
            {
                if (TryGetFreshCache(out freshEvents) && freshEvents != null)
                    return freshEvents;

                using var http = new HttpClient();
                var xmlText = await http.GetStringAsync(XmlFeedUrl);
                var parsedEvents = ParseForexEvents(xmlText);

                cachedEvents = parsedEvents;
                cacheFetchedAtUtc = DateTime.UtcNow;
                return CloneEvents(parsedEvents);
            }
            catch
            {
                if (cachedEvents != null)
                    return CloneEvents(cachedEvents);

                throw;
            }
            finally
            {
                CacheSemaphore.Release();
            }
        }

        private static List<ForexEvent> ParseForexEvents(string xmlText)
        {
            var doc = XDocument.Parse(xmlText);
            return doc.Descendants("event")
                .Select(x =>
                {
                    var date = DateTime.ParseExact(
                        x.Element("date")!.Value.Trim(),
                        "MM-dd-yyyy",
                        CultureInfo.InvariantCulture
                    );
                    var rawTime = x.Element("time")!.Value.Trim();
                    var normalizedTime = DateTime.TryParseExact(
                        rawTime,
                        "h:mmtt",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var timePart
                    )
                        ? timePart.ToString("HH:mm", CultureInfo.InvariantCulture)
                        : rawTime;

                    return new ForexEvent
                    {
                        Date = date,
                        Time = normalizedTime,
                        Currency = x.Element("country")!.Value.Trim(),
                        Event = x.Element("title")!.Value.Trim(),
                        Impact = x.Element("impact")!.Value.Trim(),
                        Forecast = x.Element("forecast")?.Value.Trim(),
                        Previous = x.Element("previous")?.Value.Trim(),
                    };
                })
                .ToList();
        }

        private static List<ForexEvent> CloneEvents(IEnumerable<ForexEvent> events)
        {
            return events
                .Select(ev => new ForexEvent
                {
                    Date = ev.Date,
                    Time = ev.Time,
                    Currency = ev.Currency,
                    Event = ev.Event,
                    Impact = ev.Impact,
                    Forecast = ev.Forecast,
                    Previous = ev.Previous,
                })
                .ToList();
        }

        private static Image? GetFlagImage(string currency)
        {
            var fileName = CurrencyFlagFiles.TryGetValue(currency, out var mappedFileName)
                ? mappedFileName
                : "none.png";

            return FlagImageCache.GetOrAdd(fileName, LoadFlagImage);
        }

        private static Image? LoadFlagImage(string fileName)
        {
            var resourceImage = LoadEmbeddedFlagImage(fileName);
            if (resourceImage != null)
                return resourceImage;

            var assemblyDirectory = Path.GetDirectoryName(typeof(MBS_Economic_Calendar_Flags).Assembly.Location);
            if (string.IsNullOrEmpty(assemblyDirectory))
                return null;

            var filePath = Path.Combine(assemblyDirectory, "Flags", fileName);
            if (!File.Exists(filePath))
                return null;

            using var stream = File.OpenRead(filePath);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }

        private static Image? LoadEmbeddedFlagImage(string fileName)
        {
            var assembly = typeof(MBS_Economic_Calendar_Flags).Assembly;
            var resourceName = $"{typeof(MBS_Economic_Calendar_Flags).Namespace}.Flags.{fileName.Replace('\\', '.').Replace('/', '.')}";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return null;

            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }

        private DateTime GetReferenceDateTimeUtc()
        {
            var referenceDateTime = Symbol?.LastDateTime ?? DateTime.UtcNow;
            return referenceDateTime.Kind == DateTimeKind.Utc
                ? referenceDateTime
                : referenceDateTime.ToUniversalTime();
        }

        private static bool TryGetEventDateTimeUtc(ForexEvent forexEvent, out DateTime eventDateTimeUtc)
        {
            eventDateTimeUtc = default;

            if (!DateTime.TryParseExact(
                forexEvent.Time,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var eventTime))
            {
                return false;
            }

            var easternDateTime = DateTime.SpecifyKind(
                forexEvent.Date.Date
                    .AddHours(eventTime.Hour)
                    .AddMinutes(eventTime.Minute),
                DateTimeKind.Unspecified);

            eventDateTimeUtc = TimeZoneInfo.ConvertTimeToUtc(easternDateTime, EasternTimeZone);
            return true;
        }

        public class ForexEvent
        {
            public DateTime Date { get; set; }
            public string Time { get; set; } = "";
            public string Currency { get; set; } = "";
            public string Event { get; set; } = "";
            public string Impact { get; set; } = "";
            public string? Forecast { get; set; }
            public string? Previous { get; set; }
        }
    }
}
