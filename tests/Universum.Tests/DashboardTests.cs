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

        // several places of one name: all are offered, told apart by district, region and height
        server.Answers["/v1/search"] = """
            {"results":[{"name":"Vymyslená Lehota","latitude":48.81,"longitude":19.56,"elevation":469.0,"country":"Slovensko","admin1":"Banskobystrický kraj","admin2":"okres Brezno"},
                        {"name":"Vymyslená Lehota","latitude":49.2,"longitude":18.7,"country":"Slovensko","admin1":"Žilinský kraj"},
                        {"name":"bez súradníc"}]}
            """;
        var places = await weather.SearchAsync("Vymyslená Lehota");
        Assert.True(places.Count == 2 && server.Requests[^1].Contains("count=10", StringComparison.Ordinal), "all places of the name: " + places.Count);
        Assert.Equal("Vymyslená Lehota – okres Brezno, Banskobystrický kraj, Slovensko · 469 m n. m.", places[0].FullDisplay);
        Assert.Equal("Vymyslená Lehota, okres Brezno", places[0].Display);
        Assert.Equal("Vymyslená Lehota – Žilinský kraj, Slovensko", places[1].FullDisplay);
        Assert.Equal("Vymyslená Lehota, Žilinský kraj", places[1].Display);

        // a place stored by 0.2.0 (without district and height) still reads
        var stored = System.Text.Json.JsonSerializer.Deserialize<WeatherPlace>(
            """{"Name":"Valaská","Region":"Banskobystrický kraj","Country":"Slovensko","Latitude":48.81,"Longitude":19.56,"Display":"Valaská, Slovensko"}""");
        Assert.True(stored is { District: null, Elevation: null, Display: "Valaská, Banskobystrický kraj" }, "older settings: " + stored?.Display);

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

        // a body that breaks off, one too large without a length, one that never ends: a WeatherException each time
        server.Status = HttpStatusCode.OK;
        foreach (var (body, what) in new (Func<Stream>, string)[]
                 {
                     (() => new BrokenStream(), "broken off"),
                     (() => new MemoryStream(new byte[(1 << 20) + 10]), "too large"),
                     (() => new EndlessStream(), "endless"),
                 })
        {
            server.Body = body;
            var failed = false;
            try
            {
                await new OpenMeteo(new HttpClient(server)) { Timeout = TimeSpan.FromSeconds(1) }.ForecastAsync(1, 2);
            }
            catch (WeatherException)
            {
                failed = true;
            }

            Assert.True(failed, "a body " + what + " is a WeatherException");
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Spojenie bolo prerušené."));
    }

    // trickles a byte now and then and never ends (the time limit stops it)
    private sealed class EndlessStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(50, cancellationToken);
            buffer.Span[0] = (byte)' ';
            return 1;
        }
    }

    private sealed class Server : HttpMessageHandler
    {
        public Dictionary<string, string> Answers { get; } = [];

        public List<string> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        /// <summary>A body as a stream without a known length (instead of the answers).</summary>
        public Func<Stream>? Body { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (Body is { } body && Status == HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLength(body())) });
            }

            return Task.FromResult(Status != HttpStatusCode.OK
                ? new HttpResponseMessage(Status)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Answers.GetValueOrDefault(request.RequestUri.AbsolutePath, "{}"), Encoding.UTF8, "application/json"),
                });
        }
    }

    // StreamContent asks the stream for its length: this one does not tell it
    private sealed class UnknownLength(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
