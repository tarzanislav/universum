using System.Net;
using System.Text;
using Universum.Core.Calendar;
using Universum.Core.Weather;

namespace Universum.Tests;

/// <summary>Dashboard: the Slovak calendar of names and the weather from Open-Meteo (invented answers).</summary>
public sealed class DashboardTests
{
    [Test]
    public void NameDaysFollowTheSlovakCalendar()
    {
        Assert.Equal("Meniny má Sabína", SlovakCalendar.NameDayText(new DateOnly(2026, 10, 27)));
        Assert.Equal("Meniny majú Adam, Eva", SlovakCalendar.NameDayText(new DateOnly(2026, 12, 24)));
        Assert.Equal("Meniny majú Alexandra, Sandra, Karina", SlovakCalendar.NameDayText(new DateOnly(2027, 1, 2)));
        Assert.Equal("Pamiatka zosnulých", SlovakCalendar.NameDayText(new DateOnly(2026, 11, 2)));
        Assert.Equal("Nový rok – Deň vzniku Slovenskej republiky", SlovakCalendar.NameDayText(new DateOnly(2027, 1, 1)));
        Assert.Equal("Radomír", SlovakCalendar.NameDay(new DateOnly(2028, 2, 29)).Single());
        Assert.Equal("Silvester", SlovakCalendar.NameDay(new DateOnly(2026, 12, 31)).Single());
        for (var day = new DateOnly(2028, 1, 1); day.Year == 2028; day = day.AddDays(1))
        {
            Assert.True(SlovakCalendar.NameDayText(day).Length > 0, "every day says something: " + day);
        }

        Assert.Equal("utorok 27. októbra 2026", SlovakCalendar.LongDate(new DateOnly(2026, 10, 27)));
    }

    [Test]
    public async Task WeatherComesFromOpenMeteo()
    {
        var server = new Server();
        server.Answers["/v1/search"] = """{"results":[{"name":"Vymyslené Mesto","latitude":48.1486,"longitude":17.1077,"country":"Slovensko","admin1":"Bratislavský kraj"}]}""";
        server.Answers["/v1/forecast"] = """
            {"current":{"temperature_2m":12.4,"apparent_temperature":10.1,"weather_code":61,"wind_speed_10m":14.2,"is_day":1},
             "daily":{"time":["2026-10-27","2026-10-28","2026-10-29"],"weather_code":[61,3,0],
                      "temperature_2m_max":[13.0,11.5,9.8],"temperature_2m_min":[6.1,5.0,2.2],"precipitation_probability_max":[80,30,5]}}
            """;
        var weather = new OpenMeteo(new HttpClient(server));
        var place = await weather.FindAsync("Vymyslené Mesto");
        Assert.True(place is { Name: "Vymyslené Mesto", Latitude: 48.1486, Country: "Slovensko" }, "the town found");
        Assert.True(server.Requests[0].StartsWith("https://geocoding-api.open-meteo.com/v1/search?", StringComparison.Ordinal) &&
                    server.Requests[0].Contains("name=Vymyslen", StringComparison.Ordinal), "only the town is sent: " + server.Requests[0]);
        var report = await weather.ForecastAsync(place!.Latitude, place.Longitude);
        Assert.True(server.Requests[1].Contains("latitude=48.1486&longitude=17.1077", StringComparison.Ordinal), "coordinates with a dot: " + server.Requests[1]);
        Assert.True(report is { Temperature: 12.4, Code: 61, IsDay: true, Days.Count: 3 } && report.Description == "slabý dážď", "now: " + report.Description);
        Assert.True(report.Days[1] is { Max: 11.5, Min: 5.0, PrecipitationChance: 30, Code: 3 }, "tomorrow");
        Assert.Equal("☂", OpenMeteo.Symbol(61));

        server.Answers["/v1/search"] = """{"generationtime_ms":0.5}""";
        Assert.Equal(null, await weather.FindAsync("Nikde"));
        server.Status = HttpStatusCode.TooManyRequests;
        var refused = false;
        try
        {
            await weather.ForecastAsync(1, 2);
        }
        catch (WeatherException)
        {
            refused = true;
        }

        Assert.True(refused, "a refusal is a WeatherException");
    }

    private sealed class Server : HttpMessageHandler
    {
        public Dictionary<string, string> Answers { get; } = [];

        public List<string> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(Status != HttpStatusCode.OK
                ? new HttpResponseMessage(Status)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Answers.GetValueOrDefault(request.RequestUri.AbsolutePath, "{}"), Encoding.UTF8, "application/json"),
                });
        }
    }
}
