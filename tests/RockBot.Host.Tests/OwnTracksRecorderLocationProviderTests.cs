using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// <see cref="OwnTracksRecorderLocationProvider"/> against canned <c>/api/0/last</c> responses (#740).
/// </summary>
[TestClass]
public sealed class OwnTracksRecorderLocationProviderTests
{
    // The shape a live Recorder returns for ?fields=tst,lat,lon,acc,tzname,locality,addr,cc
    // (coordinates are made up). Without a geocoder key there is no locality or cc.
    private const string TwoDevices = """
        [
          {"tst":1793354592,"lat":52.3740,"lon":4.8897,"acc":3,"tzname":"Europe/Amsterdam"},
          {"tst":1793350000,"lat":41.8781,"lon":-87.6298,"acc":12,"tzname":"America/Chicago","locality":"Chicago","cc":"US"}
        ]
        """;

    private static OwnTracksRecorderLocationProvider Make(StubHandler handler, string? device = null) =>
        new(Options.Create(new UserLocationOptions
        {
            OwnTracks = { BaseUrl = "http://recorder.test:8083/", User = "rocky", Device = device }
        }), handler);

    [TestMethod]
    public async Task GetLatest_PicksNewestFix()
    {
        var provider = Make(new StubHandler(TwoDevices));

        var fix = await provider.GetLatestAsync(CancellationToken.None);

        Assert.IsNotNull(fix);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1793354592), fix.ReportedAt);
        Assert.AreEqual("Europe/Amsterdam", fix.TimeZoneId);
        Assert.AreEqual(52.3740, fix.Latitude, 1e-9);
        Assert.AreEqual(3, fix.AccuracyMeters);
        Assert.IsNull(fix.Locality);
        Assert.IsNull(fix.CountryCode);
    }

    [TestMethod]
    public async Task GetLatest_ReadsLocalityAndCountry()
    {
        var provider = Make(new StubHandler(
            """[{"tst":1793350000,"lat":41.9,"lon":-87.6,"tzname":"America/Chicago","locality":"Chicago","cc":"US"}]"""));

        var fix = await provider.GetLatestAsync(CancellationToken.None);

        Assert.AreEqual("Chicago", fix!.Locality);
        Assert.AreEqual("US", fix.CountryCode);
        Assert.IsNull(fix.AccuracyMeters);
    }

    [TestMethod]
    public async Task GetLatest_EmptyArray_ReturnsNull()
    {
        var provider = Make(new StubHandler("[]"));

        Assert.IsNull(await provider.GetLatestAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task GetLatest_RequestsOnlyTheFieldsItUses()
    {
        var handler = new StubHandler("[]");
        var provider = Make(handler, device: "rockyphone");

        await provider.GetLatestAsync(CancellationToken.None);

        Assert.AreEqual(
            "http://recorder.test:8083/api/0/last?user=rocky&device=rockyphone&fields=tst,lat,lon,acc,tzname,locality,addr,cc",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.AreEqual(HttpMethod.Get, handler.LastRequest.Method);
    }

    [TestMethod]
    public async Task GetLatest_NoDevice_OmitsDeviceParameter()
    {
        var handler = new StubHandler("[]");
        var provider = Make(handler);

        await provider.GetLatestAsync(CancellationToken.None);

        Assert.AreEqual(
            "http://recorder.test:8083/api/0/last?user=rocky&fields=tst,lat,lon,acc,tzname,locality,addr,cc",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [TestMethod]
    public async Task GetLatest_NoLocality_TakesTheCityFromAddr_AndKeepsNoStreet()
    {
        // What the live Recorder (OpenCage) stores: addr and cc, no locality.
        var provider = Make(new StubHandler("""
            [{"tst":1793465509,"lat":39.7990,"lon":-89.6440,"acc":3,"tzname":"America/Chicago",
              "cc":"US","addr":"unnamed road, Springfield, IL 62701, USA"}]
            """));

        var fix = await provider.GetLatestAsync(CancellationToken.None);

        Assert.AreEqual("Springfield", fix!.Locality);
        Assert.AreEqual("US", fix.CountryCode);
        Assert.IsFalse(fix.ToString().Contains("unnamed road"), fix.ToString());
    }

    [TestMethod]
    public async Task GetLatest_LocalityWinsOverAddr()
    {
        var provider = Make(new StubHandler("""
            [{"tst":1793465509,"lat":39.80,"lon":-89.64,"locality":"Springfield","addr":"1 Main St, Chatham, IL 62629, USA"}]
            """));

        Assert.AreEqual("Springfield", (await provider.GetLatestAsync(CancellationToken.None))!.Locality);
    }

    [TestMethod]
    [DataRow("unnamed road, Springfield, IL 62701, USA", "Springfield")]
    [DataRow("Springfield, IL 62701, USA", null)] // first part could be a street: fall back to coordinates
    [DataRow("Damrak 1, 1012 LG Amsterdam, Netherlands", "Amsterdam")]
    [DataRow("10 Downing Street, London SW1A 2AA, United Kingdom", "London")]
    [DataRow("Invalidenstraße 1, 10115 Berlin, Germany", "Berlin")]
    [DataRow("1 Rue de Rivoli, 75001 Paris, France", "Paris")]
    [DataRow("Some Street 5, Netherlands", null)]
    [DataRow("Netherlands", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void LocalityFromAddress_KeepsOnlyTheCity(string? addr, string? expected) =>
        Assert.AreEqual(expected, OwnTracksRecorderLocationProvider.LocalityFromAddress(addr));

    [TestMethod]
    public async Task GetLatest_HttpError_Throws()
    {
        var provider = Make(new StubHandler("oops", HttpStatusCode.BadGateway));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => provider.GetLatestAsync(CancellationToken.None));
    }

    internal sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
