using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

string baseUrl = (Environment.GetEnvironmentVariable("V23_BASE_URL")
    ?? throw new InvalidOperationException("Set V23_BASE_URL")).TrimEnd('/');
string clientUser = Environment.GetEnvironmentVariable("V23_CLIENT_USER")
    ?? throw new InvalidOperationException("Set V23_CLIENT_USER");
string clientPass = Environment.GetEnvironmentVariable("V23_CLIENT_PASS")
    ?? throw new InvalidOperationException("Set V23_CLIENT_PASS");
string userAgent = Environment.GetEnvironmentVariable("V23_USER_AGENT")
    ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

// Finds the first property whose lower-cased name contains all given substrings -
// camelCase can lower-case acronym-heavy names (e.g. HTUKey) unpredictably.
static JsonNode? FindValue(JsonNode? obj, string[] substrings, out string? foundKey, bool recursive = false)
{
    foundKey = null;
    if (obj is not JsonObject o) return null;
    foreach (var (k, v) in o)
    {
        var lk = k.ToLowerInvariant();
        if (substrings.All(s => lk.Contains(s))) { foundKey = k; return v; }
    }
    if (recursive)
    {
        foreach (var (_, v) in o)
        {
            if (v is JsonObject)
            {
                var nested = FindValue(v, substrings, out var nk, true);
                if (nk != null) { foundKey = nk; return nested; }
            }
        }
    }
    return null;
}

// 1. GetAPIBearer -------------------------------------------------------------
var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientUser}:{clientPass}"));
var bearerReq = new HttpRequestMessage(HttpMethod.Get, "backend/Account/GetAPIBearer");
bearerReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
var bearerResp = await http.SendAsync(bearerReq);
bearerResp.EnsureSuccessStatusCode();
var bearerRaw = await bearerResp.Content.ReadAsStringAsync();
string bearer = bearerRaw.StartsWith('"') ? JsonSerializer.Deserialize<string>(bearerRaw)! : bearerRaw.Trim();

http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

// 2-3. Destination search -------------------------------------------------------
var predResp = await http.PostAsJsonAsync("backend/Utils/GetGooglePrediction",
    new { query = "Berlin, Germany", prefix = "htl" });
var predJson = JsonNode.Parse(await predResp.Content.ReadAsStringAsync())!;
var googlePred = JsonNode.Parse(predJson["googleResponse"]!.GetValue<string>())!;
string placeId = googlePred["predictions"]![0]!["place_id"]!.GetValue<string>();

var detResp = await http.PostAsJsonAsync("backend/Utils/GetGooglePredictionDetails", new { placeId });
var detJson = JsonNode.Parse(await detResp.Content.ReadAsStringAsync())!;
var googleDet = JsonNode.Parse(detJson["googleResponse"]!.GetValue<string>())!;
var place = googleDet["result"]!;
var loc = place["geometry"]!["location"]!;
string countryCode = place["address_components"]!.AsArray()
    .First(c => c!["types"]!.AsArray().Any(t => t!.GetValue<string>() == "country"))!["short_name"]!.GetValue<string>();
string city = place["name"]?.GetValue<string>() ?? place["formatted_address"]!.GetValue<string>();
double lat = loc["lat"]!.GetValue<double>(), lon = loc["lng"]!.GetValue<double>();

// 4. BeginHotelSearch -------------------------------------------------------------
var checkIn = DateTime.Now.AddDays(30).ToString("yyyy-MM-dd");
var checkOut = DateTime.Now.AddDays(33).ToString("yyyy-MM-dd");
var beginResp = await http.PostAsJsonAsync("backend/Hotels/BeginHotelSearch", new
{
    checkIn, checkOut, starRating = 0, nationality = "IL", searchRadius = 8,
    roomsString = "2;", recaptchaToken = "", city, id = countryCode.ToUpperInvariant(), lat, lon,
});
var beginJson = JsonNode.Parse(await beginResp.Content.ReadAsStringAsync())!;
string searchToken = beginJson["searchToken"]!.GetValue<string>();
string sysToken = beginJson["sysToken"]!.GetValue<string>();

// 5. GetHotels (poll) ---------------------------------------------------------------
var hotels = new JsonArray();
var deadline = DateTime.UtcNow.AddSeconds(60);
while (DateTime.UtcNow < deadline)
{
    var r = await http.PostAsJsonAsync("backend/Hotels/GetHotels", new { searchToken, sysToken });
    var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    foreach (var h in j["hotels"]?.AsArray() ?? new JsonArray())
        hotels.Add(h!.DeepClone());
    if (j["poolingFinished"]?.GetValue<bool>() == true) break;
    await Task.Delay(1500);
}

// Pick the first hotel/room with a usable hotelUkey/roomBToken
string? hotelUkey = null, roomBToken = null, hotelItemCode = null;
foreach (var hotel in hotels)
{
    var hUkey = FindValue(hotel, new[] { "ukey" }, out _);
    var roomClasses = FindValue(hotel, new[] { "roomclass" }, out _)?.AsArray();
    if (hUkey is null || roomClasses is null) continue;
    var item = FindValue(hotel, new[] { "item" }, out _);
    hotelItemCode = item is not null ? FindValue(item, new[] { "code" }, out _)?.GetValue<string>() : null;

    foreach (var room in roomClasses)
    {
        var hotelRooms = FindValue(room, new[] { "hotelroom" }, out _)?.AsArray();
        if (hotelRooms is null || hotelRooms.Count == 0) continue;
        var btoken = FindValue(hotelRooms[0], new[] { "token" }, out _);
        if (btoken is null) continue;
        hotelUkey = hUkey.GetValue<string>();
        roomBToken = btoken.GetValue<string>();
        break;
    }
    if (roomBToken is not null) break;
}
if (hotelUkey is null || roomBToken is null)
    throw new InvalidOperationException("No bookable hotel/room found for this search");

// 6-7. HotelInfo / ChargeConditions (optional, informational) ------------------------
await http.PostAsync(
    $"backend/Hotels/HotelInfo?searchToken={Uri.EscapeDataString(searchToken)}" +
    $"&hotelUkey={Uri.EscapeDataString(hotelUkey)}&RoomBToken={Uri.EscapeDataString(roomBToken)}",
    content: null);
await http.PostAsJsonAsync("backend/Hotels/ChargeConditions",
    new { searchToken, roomBTokenList = new[] { roomBToken }, language = "He" });

// 8. BeginOneHotelSearch ----------------------------------------------------------------
var oneResp = await http.PostAsJsonAsync("backend/Hotels/BeginOneHotelSearch", new
{
    searchToken,
    hotels = new[] { new { roomBToken, hotelItemCode = hotelItemCode ?? "", multiRoomsBTokens = Array.Empty<string>() } },
});
var oneJson = JsonNode.Parse(await oneResp.Content.ReadAsStringAsync())!;
string oneHotelToken = oneJson["searchToken"]!.GetValue<string>();

// 9. GetOneHotelSearchData (poll) + refresh selection ---------------------------------
string refreshedHotelUkey = hotelUkey, refreshedRoomBToken = roomBToken, refreshedRoomId = "";
deadline = DateTime.UtcNow.AddSeconds(60);
while (DateTime.UtcNow < deadline)
{
    var r = await http.PostAsJsonAsync("backend/Hotels/GetOneHotelSearchData", new { searchToken = oneHotelToken });
    var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    var pool = j["hotels"]?.AsArray();
    if (pool is { Count: > 0 })
    {
        var hotelObj = FindValue(pool[0], new[] { "hotel" }, out _, recursive: true) ?? pool[0];
        var ukey = FindValue(hotelObj, new[] { "ukey" }, out _, recursive: true);
        var roomClasses = FindValue(hotelObj, new[] { "roomclass" }, out _, recursive: true)?.AsArray();
        var room = roomClasses is { Count: > 0 } ? roomClasses[0] : null;
        var btoken = room is not null ? FindValue(room, new[] { "token" }, out _) : null;
        var roomId = room is not null ? FindValue(room, new[] { "uniquekey" }, out _) : null;
        refreshedHotelUkey = ukey?.GetValue<string>() ?? refreshedHotelUkey;
        refreshedRoomBToken = btoken?.GetValue<string>() ?? refreshedRoomBToken;
        refreshedRoomId = roomId?.GetValue<string>() ?? "";
    }
    if (j["poolingFinished"]?.GetValue<bool>() == true) break;
    await Task.Delay(1500);
}

// 10. Book (HELD, unpaid, unconfirmed) --------------------------------------------------
var bookResp = await http.PostAsync(
    $"backend/Hotels/Book?hotelUkey={Uri.EscapeDataString(refreshedHotelUkey)}" +
    $"&searchToken={Uri.EscapeDataString(oneHotelToken)}&roomId={Uri.EscapeDataString(refreshedRoomId)}" +
    "&Language=He&afterOneHotelSearch=true",
    content: null);
var bookJson = JsonNode.Parse(await bookResp.Content.ReadAsStringAsync())!;
Console.WriteLine($"Held OrderId: {bookJson["orderId"]}  totalPrice={bookJson["totalPrice"]} {bookJson["currency"]}");

// Step 11 (BookComplete) intentionally NOT called here - see the wiki's BookComplete page.
