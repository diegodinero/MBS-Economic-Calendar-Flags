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
        private bool showHoverInfo = true;
        private bool showPastEvents = false;


        //–– Runtime state
        private List<ForexEvent>? allEvents;
        private List<ForexEvent>? forexEvents;
        private Exception? fetchError;
        private Font font = new Font("Consolas", 10f);
        private Font boldFont = new Font("Consolas", 10f, FontStyle.Bold);
        private Font headerFont = new Font("Consolas", 12f, FontStyle.Bold);
        private readonly object lockObject = new object();
        private int newsPositionX = 500;
        private int newsPositionY = 10;
        private static readonly TimeZoneInfo EasternTimeZone = ResolveEasternTimeZone();
        private static readonly SemaphoreSlim CacheSemaphore = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<string, Image?> FlagImageCache = new ConcurrentDictionary<string, Image?>(StringComparer.OrdinalIgnoreCase);
        private static readonly IReadOnlyDictionary<string, string> CurrencyCountryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AUD"] = "Australia",
            ["CAD"] = "Canada",
            ["CHF"] = "Switzerland",
            ["CNY"] = "China",
            ["EUR"] = "Euro Zone",
            ["GBP"] = "United Kingdom",
            ["JPY"] = "Japan",
            ["NZD"] = "New Zealand",
            ["USD"] = "United States"
        };
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
        private const int FlagImageSize = FlagMarkerSize - FlagInnerPadding * 2;
        private const int FlagStackSpacing = 4;
        private const int FlagBottomMargin = 2;
        private const int EventCardWidth = 235;
        private const int EventCardPadding = 6;
        private const int NewsTableWidth = 799;
        private const int NewsDateColWidth = 110;
        private const int NewsTimeColWidth = 60;
        private const int NewsCurrencyColWidth = 100;
        private const int NewsImpactColWidth = 96;
        private const int NewsHeaderHeight = 24;
        private const int NewsRowHeight = 22;
        private const int NewsImpactCircleSize = 10;

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

            // Build all three font objects before touching the fields so that a repaint
            // that fires mid-initialization never sees a mismatched set of fonts.
            Font? selectedFont = null;
            foreach (var fam in new[] { "Droid Sans Mono", "DejaVu Sans Mono", "Consolas", "Verdana" })
            {
                var test = new Font(fam, 11f);
                if (test.Name == fam)
                {
                    selectedFont = test;
                    break;
                }
                test.Dispose();
            }
            selectedFont ??= new Font("Consolas", 11f);

            var newBold   = new Font(selectedFont.FontFamily, selectedFont.Size, FontStyle.Bold);
            var newHeader = new Font(selectedFont.FontFamily, 13f, FontStyle.Bold);

            font       = selectedFont;
            boldFont   = newBold;
            headerFont = newHeader;

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
                        // Use Today for current date when chart is at current time
                        var now = DateTime.Now;
                        var chartDateTime = Symbol?.LastDateTime ?? DateTime.MinValue;

                        // If Symbol.LastDateTime is not initialized or is showing current/recent time
                        DateTime chartDate;
                        if (chartDateTime == DateTime.MinValue || 
                            chartDateTime.Year < 2000 || 
                            Math.Abs((now - chartDateTime).TotalHours) < 24)
                        {
                            // Use today's date
                            chartDate = now.Date;
                        }
                        else
                        {
                            // Use the chart's date
                            chartDate = chartDateTime.Date;
                        }

                        Debug.WriteLine($"[EconomicEventsIndicator] Chart DateTime: {chartDateTime}, Using Date: {chartDate:MM/dd/yyyy}");
                        Debug.WriteLine($"[EconomicEventsIndicator] Total events in cache: {allEvents.Count}");

                        temp = allEvents.Where(e => e.Date.Date == chartDate).ToList();

                        Debug.WriteLine($"[EconomicEventsIndicator] Events matching {chartDate:MM/dd/yyyy}: {temp.Count}");
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
                if (!allowed.Contains(NormalizeCurrencyCode(e.Currency)))
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
                string header;
                if (dateMode == 1)
                {
                    var now = DateTime.Now;
                    var chartDateTime = Symbol?.LastDateTime ?? DateTime.MinValue;

                    // If Symbol.LastDateTime is not initialized or is showing current/recent time
                    DateTime chartDate;
                    if (chartDateTime == DateTime.MinValue || 
                        chartDateTime.Year < 2000 || 
                        Math.Abs((now - chartDateTime).TotalHours) < 24)
                    {
                        // Use today's date
                        chartDate = now.Date;
                    }
                    else
                    {
                        // Use the chart's date
                        chartDate = chartDateTime.Date;
                    }

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
                    DrawNewsTable(g, forexEvents.OrderBy(ParseEventDateTimeForSorting), x + 2, y, rect.Right, rect.Bottom);
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

                }

                var hoveredFlag = DrawEventFlags(g, rect, eventTimeUtc => (float)conv.GetChartX(eventTimeUtc), args.MousePosition);
                if (showHoverInfo && hoveredFlag != null)
                {
                    int cardHeight = GetEventCardHeight();
                    int cardX = hoveredFlag.MarkerBounds.Right + 8;
                    int cardY = hoveredFlag.MarkerBounds.Top - (cardHeight / 2);

                    cardY = Math.Max(rect.Top, Math.Min(cardY, rect.Bottom - cardHeight));

                    DrawEventCard(g, hoveredFlag.Event, cardX, cardY);
                }
            }
        }

        private HoveredFlagInfo? DrawEventFlags(Graphics graphics, Rectangle rect, Func<DateTime, float> getChartX, Point mousePosition)
        {
            if (forexEvents == null)
                return null;

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

            HoveredFlagInfo? hoveredEvent = null;
            foreach (var group in groupedEvents)
            {
                float xCoord = getChartX(group.Key);
                if (xCoord < rect.Left - FlagMarkerSize || xCoord > rect.Right + FlagMarkerSize)
                    continue;

                int stackIndex = 0;
                foreach (var ev in group.Value
                    .OrderBy(e => GetImpactPriority(e.Impact))
                    .ThenBy(e => e.Currency, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Event, StringComparer.OrdinalIgnoreCase))
                {
                    var flagImage = GetFlagImage(ev.Currency);
                    if (flagImage == null)
                        continue;

                    int drawX = (int)Math.Round(xCoord - (FlagMarkerSize / 2f));
                    int drawY = rect.Bottom - FlagBottomMargin - FlagMarkerSize - (stackIndex * (FlagMarkerSize + FlagStackSpacing));
                    if (drawY < rect.Top)
                        break;

                    var markerBounds = new Rectangle(drawX, drawY, FlagMarkerSize, FlagMarkerSize);
                    DrawFlagMarker(
                        graphics,
                        flagImage,
                        markerBounds,
                        GetImpactColor(ev.Impact));

                    if (markerBounds.Contains(mousePosition))
                        hoveredEvent = new HoveredFlagInfo(ev, markerBounds);

                    stackIndex++;
                }
            }

            return hoveredEvent;
        }

        private void DrawNewsTable(Graphics graphics, IEnumerable<ForexEvent> events, int x, int y, int right, int bottom)
        {
            var groups = events.GroupBy(ev => ev.Date.Date).ToList();
            if (groups.Count == 0)
                return;

            int tableWidth = NewsTableWidth;
            int tableRight = Math.Min(right - 4, x + tableWidth);
            if (tableRight - x < tableWidth)
            {
                x = Math.Max(0, tableRight - tableWidth);
                tableRight = x + tableWidth;
            }

            if (tableWidth <= 0)
                return;

            int dateColWidth = NewsDateColWidth;
            int timeColWidth = NewsTimeColWidth;
            int currencyColWidth = NewsCurrencyColWidth;
            int impactColWidth = NewsImpactColWidth;
            int newsColWidth = tableWidth - dateColWidth - timeColWidth - currencyColWidth - impactColWidth;
            int headerHeight = NewsHeaderHeight;
            int rowHeight = NewsRowHeight;

            using var tableBg = new SolidBrush(Color.FromArgb(255, 40, 45, 65));
            using var headerBg = new SolidBrush(Color.FromArgb(255, 47, 73, 132));
            using var dateCellBg = new SolidBrush(Color.FromArgb(255, 50, 56, 78));
            using var borderPen = new Pen(Color.FromArgb(255, 25, 30, 45), 1f);
            using var gridPen = new Pen(Color.FromArgb(140, 160, 160, 160), 1f);
            using var separatorPen = new Pen(Color.FromArgb(200, 80, 90, 110), 1f); // More visible separator
            int estimatedHeight = headerHeight + groups.Sum(group => Math.Max(1, group.Count()) * rowHeight);
            int tableBottom = Math.Min(bottom, y + estimatedHeight);
            graphics.FillRectangle(tableBg, x, y, tableWidth, Math.Max(1, tableBottom - y));
            graphics.DrawRectangle(borderPen, x, y, tableWidth - 1, Math.Max(0, tableBottom - y - 1));

            DrawTableHeader(graphics, x, y, dateColWidth, timeColWidth, currencyColWidth, impactColWidth, newsColWidth, headerHeight, tableRight, headerBg, gridPen);

            int cy = y + headerHeight;
            foreach (var group in groups)
            {
                var groupEvents = group.ToList();
                int groupHeight = groupEvents.Count * rowHeight;
                if (cy + groupHeight > bottom)
                    break;

                graphics.FillRectangle(dateCellBg, x, cy, dateColWidth, groupHeight);
                graphics.DrawLine(gridPen, x + dateColWidth, cy, x + dateColWidth, cy + groupHeight);

                // Draw horizontal separator at top of each date group (full table width)
                graphics.DrawLine(separatorPen, x, cy, x + tableWidth - 1, cy);

                using (var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                })
                {
                    graphics.DrawString(group.Key.ToString("ddd MMM dd", CultureInfo.InvariantCulture), font, Brushes.Gainsboro, new RectangleF(x + 2, cy, dateColWidth - 4, groupHeight), format);
                }

                for (int i = 0; i < groupEvents.Count; i++)
                {
                    var ev = groupEvents[i];
                    if (cy + rowHeight > bottom)
                        return;

                    bool isFirstInGroup = i == 0;
                    bool isLastInGroup = i == groupEvents.Count - 1;
                    DrawEventRow(graphics, ev, x, cy, dateColWidth, timeColWidth, currencyColWidth, impactColWidth, newsColWidth, rowHeight, tableRight, gridPen, separatorPen, isFirstInGroup);
                    cy += rowHeight;
                }
            }
        }

        private void DrawTableHeader(Graphics graphics, int x, int y, int dateColWidth, int timeColWidth, int currencyColWidth, int impactColWidth, int newsColWidth, int headerHeight, int tableRight, Brush headerBg, Pen gridPen)
        {
            graphics.FillRectangle(headerBg, x, y, tableRight - x, headerHeight);
            graphics.DrawLine(gridPen, x, y + headerHeight, tableRight, y + headerHeight);

            int cx = x;
            DrawHeaderCell(graphics, "Date", cx, y, dateColWidth, headerHeight);
            cx += dateColWidth;
            graphics.DrawLine(gridPen, cx, y, cx, y + headerHeight);

            DrawHeaderCell(graphics, "Time", cx, y, timeColWidth, headerHeight);
            cx += timeColWidth;
            graphics.DrawLine(gridPen, cx, y, cx, y + headerHeight);

            DrawHeaderCell(graphics, "Currency", cx, y, currencyColWidth, headerHeight);
            cx += currencyColWidth;
            graphics.DrawLine(gridPen, cx, y, cx, y + headerHeight);

            DrawHeaderCell(graphics, "Impact", cx, y, impactColWidth, headerHeight);
            cx += impactColWidth;
            graphics.DrawLine(gridPen, cx, y, cx, y + headerHeight);

            DrawHeaderCell(graphics, "Forex Factory News", cx, y, newsColWidth, headerHeight);
        }

        private void DrawHeaderCell(Graphics graphics, string text, int x, int y, int width, int height)
        {
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            graphics.DrawString(text, headerFont, Brushes.White, new RectangleF(x + 2, y, width - 4, height), format);
        }

        private void DrawEventRow(Graphics graphics, ForexEvent ev, int x, int y, int dateColWidth, int timeColWidth, int currencyColWidth, int impactColWidth, int newsColWidth, int rowHeight, int tableRight, Pen gridPen, Pen separatorPen, bool isFirstRow)
        {
            // Draw horizontal separator at the TOP of the row (except for first row)
            if (!isFirstRow)
            {
                graphics.DrawLine(separatorPen, x + dateColWidth, y, tableRight, y);
            }

            using var rowBg = new SolidBrush(Color.FromArgb(255, 62, 67, 88));
            graphics.FillRectangle(rowBg, x + dateColWidth, y, tableRight - (x + dateColWidth), rowHeight);

            int cx = x;
            cx += dateColWidth;

            DrawCellText(graphics, ev.Time, font, Brushes.Gainsboro, cx, y, timeColWidth, rowHeight, StringAlignment.Center);
            cx += timeColWidth;
            graphics.DrawLine(gridPen, cx, y, cx, y + rowHeight);

            DrawCellText(graphics, NormalizeCurrencyCode(ev.Currency), font, Brushes.Gainsboro, cx, y, currencyColWidth, rowHeight, StringAlignment.Center);
            cx += currencyColWidth;
            graphics.DrawLine(gridPen, cx, y, cx, y + rowHeight);

            DrawImpactCircle(graphics, GetImpactColor(ev.Impact), cx, y, impactColWidth, rowHeight);
            cx += impactColWidth;
            graphics.DrawLine(gridPen, cx, y, cx, y + rowHeight);

            DrawCellText(graphics, ev.Event, font, Brushes.Gainsboro, cx, y, newsColWidth, rowHeight, StringAlignment.Center);
        }

        private void DrawCellText(Graphics graphics, string text, Font textFont, Brush brush, int x, int y, int width, int height, StringAlignment alignment)
        {
            using var format = new StringFormat
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            var rect = new RectangleF(x + 4, y, Math.Max(1, width - 8), height);
            graphics.DrawString(text, textFont, brush, rect, format);
        }

        private void DrawImpactCircle(Graphics graphics, Color color, int x, int y, int width, int height)
        {
            int size = Math.Min(NewsImpactCircleSize, Math.Min(width - 6, height - 6));
            int cx = x + (width - size) / 2;
            int cy = y + (height - size) / 2;
            var state = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var fillBrush = new SolidBrush(color);
                using var borderPen = new Pen(Color.FromArgb(200, Color.Black), 1f);
                graphics.FillEllipse(fillBrush, cx, cy, size, size);
                graphics.DrawEllipse(borderPen, cx, cy, size, size);
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        private int GetEventCardHeight()
        {
            int lh = font.Height;
            int blh = boldFont.Height;
            return EventCardPadding + lh + 2 + blh + 2 + lh + 8 + lh + 2 + lh + EventCardPadding;
        }

        private void DrawEventCard(Graphics graphics, ForexEvent ev, int x, int y)
        {
            int cardWidth = EventCardWidth;
            int cardPad = EventCardPadding;
            int colWidth = (cardWidth - cardPad * 2) / 3;

            Brush impactBrush =
                ev.Impact.Equals("High", StringComparison.OrdinalIgnoreCase) ? Brushes.Red :
                ev.Impact.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? Brushes.Orange :
                ev.Impact.Equals("Low", StringComparison.OrdinalIgnoreCase) ? Brushes.Green :
                Brushes.White;

            int lh = font.Height;
            int blh = boldFont.Height;
            int cardHeight = GetEventCardHeight();

            using (var bgBrush = new SolidBrush(Color.FromArgb(255, 35, 35, 35)))
                graphics.FillRectangle(bgBrush, x, y, cardWidth, cardHeight);
            using (var borderPen = new Pen(Color.FromArgb(100, 150, 150, 150), 1f))
                graphics.DrawRectangle(borderPen, x, y, cardWidth - 1, cardHeight - 1);

            // Draw vertical colored bar on the left side matching the impact color
            float barX = x;
            float barWidth = 3;
            float barHeight = cardHeight;
            float barY = y;
            graphics.FillRectangle(impactBrush, barX, barY, barWidth, barHeight);

            int cy = y + cardPad;

            graphics.DrawString(GetCountryDisplayName(ev.Currency), font, Brushes.DarkGray, x + cardPad, cy);
            cy += lh + 2;

            graphics.DrawString(ev.Event, boldFont, impactBrush, x + cardPad, cy);
            cy += blh + 2;

            string dateTimeStr = ev.Date.ToString("dd MMM yy", CultureInfo.InvariantCulture) + "   " + ev.Time;
            graphics.DrawString(dateTimeStr, font, Brushes.DarkGray, x + cardPad, cy);
            cy += lh + 4;

            graphics.DrawLine(Pens.DimGray, x + cardPad, cy, x + cardWidth - cardPad, cy);
            cy += 4;

            graphics.DrawString("Actual", font, Brushes.DarkGray, x + cardPad, cy);
            graphics.DrawString("Forecast", font, Brushes.DarkGray, x + cardPad + colWidth, cy);
            graphics.DrawString("Previous", font, Brushes.DarkGray, x + cardPad + colWidth * 2, cy);
            cy += lh + 2;

            graphics.DrawString(string.IsNullOrEmpty(ev.Actual) ? "—" : ev.Actual, boldFont, Brushes.White, x + cardPad, cy);
            graphics.DrawString(string.IsNullOrEmpty(ev.Forecast) ? "—" : ev.Forecast, font, Brushes.White, x + cardPad + colWidth, cy);
            graphics.DrawString(string.IsNullOrEmpty(ev.Previous) ? "—" : ev.Previous, font, Brushes.White, x + cardPad + colWidth * 2, cy);
        }

        private sealed class HoveredFlagInfo
        {
            public HoveredFlagInfo(ForexEvent ev, Rectangle markerBounds)
            {
                Event = ev;
                MarkerBounds = markerBounds;
            }

            public ForexEvent Event { get; }
            public Rectangle MarkerBounds { get; }
        }

        private static void DrawFlagMarker(Graphics graphics, Image flagImage, Rectangle bounds, Color impactColor)
        {
            using var outlinePen = new Pen(impactColor, 2f);
            using var backgroundBrush = new SolidBrush(Color.FromArgb(70, impactColor));
            using var clipPath = new GraphicsPath();
            clipPath.AddEllipse(bounds);

            var state = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillEllipse(backgroundBrush, bounds);
                graphics.SetClip(clipPath);
                // Image is pre-scaled to FlagImageSize×FlagImageSize; use NearestNeighbor for
                // a crisp, cost-free 1:1 blit. PixelOffsetMode.Half avoids half-pixel drift.
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(flagImage, Rectangle.Inflate(bounds, -FlagInnerPadding, -FlagInnerPadding));
            }
            finally
            {
                graphics.Restore(state);
            }

            graphics.DrawEllipse(outlinePen, bounds);
        }

        private static Color GetImpactColor(string impact) =>
            impact.Equals("High", StringComparison.OrdinalIgnoreCase) ? Color.Red :
            impact.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? Color.Orange :
            impact.Equals("Low", StringComparison.OrdinalIgnoreCase) ? Color.Green :
            Color.White;

        private static int GetImpactPriority(string impact) =>
            impact.Equals("High", StringComparison.OrdinalIgnoreCase) ? 0 :
            impact.Equals("Medium", StringComparison.OrdinalIgnoreCase) ? 1 :
            impact.Equals("Low", StringComparison.OrdinalIgnoreCase) ? 2 :
            3;

        private static string GetCountryDisplayName(string currency)
        {
            var normalized = NormalizeCurrencyCode(currency);
            return CurrencyCountryNames.TryGetValue(normalized, out var country)
                ? $"{country}, {normalized}"
                : normalized;
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
            boldFont.Dispose();
            headerFont.Dispose();
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
                settings.Add(new SettingItemBoolean("showHoverInfo", showHoverInfo) { Text = "Show Hover Info" });
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
                if (SettingItemExtensions.TryGetValue<bool>(value, "showHoverInfo", out var shi)) showHoverInfo = shi;
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
                        Actual = x.Element("actual")?.Value.Trim(),
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
                    Actual = ev.Actual,
                    Forecast = ev.Forecast,
                    Previous = ev.Previous,
                })
                .ToList();
        }

        private static Image? GetFlagImage(string currency)
        {
            var normalizedCurrency = NormalizeCurrencyCode(currency);
            var fileName = CurrencyFlagFiles.TryGetValue(normalizedCurrency, out var mappedFileName)
                ? mappedFileName
                : "none.png";

            return FlagImageCache.GetOrAdd(fileName, LoadFlagImage);
        }

        private static string NormalizeCurrencyCode(string currency)
        {
            if (string.IsNullOrWhiteSpace(currency))
                return string.Empty;

            var trimmed = currency.Trim();
            if (CurrencyFlagFiles.ContainsKey(trimmed))
                return trimmed;

            var tokens = trimmed.Split(new[] { '/', '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                if (CurrencyFlagFiles.ContainsKey(token))
                    return token;
            }

            if (trimmed.Length >= 6)
            {
                var first = trimmed.Substring(0, 3);
                if (CurrencyFlagFiles.ContainsKey(first))
                    return first;

                var second = trimmed.Substring(3, 3);
                if (CurrencyFlagFiles.ContainsKey(second))
                    return second;
            }

            if (trimmed.Length >= 3)
            {
                var prefix = trimmed.Substring(0, 3);
                if (CurrencyFlagFiles.ContainsKey(prefix))
                    return prefix;
            }

            return trimmed;
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
            return ScaleFlagImage(image);
        }

        private static Image? LoadEmbeddedFlagImage(string fileName)
        {
            var assembly = typeof(MBS_Economic_Calendar_Flags).Assembly;
            var resourceName = $"{typeof(MBS_Economic_Calendar_Flags).Namespace}.Flags.{fileName.Replace('\\', '.').Replace('/', '.')}";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return null;

            using var image = Image.FromStream(stream);
            return ScaleFlagImage(image);
        }

        /// <summary>
        /// Scales a flag image to the exact display size using high-quality bicubic interpolation.
        /// Pre-scaling once at load time avoids repeated scaling on every paint frame.
        /// </summary>
        private static Bitmap ScaleFlagImage(Image source)
        {
            var scaled = new Bitmap(FlagImageSize, FlagImageSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(source, 0, 0, FlagImageSize, FlagImageSize);
            return scaled;
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
            public string? Actual { get; set; }
            public string? Forecast { get; set; }
            public string? Previous { get; set; }
        }
    }
}
