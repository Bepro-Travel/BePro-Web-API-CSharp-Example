# BePro Web API — C# Example

.NET 6+ console app (`HttpClient` + `System.Text.Json`, no third-party packages) walking through the full [BePro Hotel Search & Booking API](https://github.com/Bepro-Travel/BePro-Web-API/wiki) flow — steps 1–10 (everything up to, but not including, `BookComplete`, which is left for you to call deliberately once you've reviewed [BookComplete](https://github.com/Bepro-Travel/BePro-Web-API/wiki/BookComplete)).

The app creates a **held, unpaid** booking and prints its `orderId`, without ever calling `BookComplete`.

## Prerequisites

| Setting | Meaning |
|---|---|
| `V23_BASE_URL` | Base URL of the API for your account, e.g. `https://your_company.beprotravel.com` (no trailing slash) |
| `V23_CLIENT_USER` / `V23_CLIENT_PASS` | Basic Auth credentials for [`GetAPIBearer`](https://github.com/Bepro-Travel/BePro-Web-API/wiki/GetAPIBearer), issued to you separately |
| `V23_USER_AGENT` | Optional — must look like a real browser (see [Authentication & Setup](https://github.com/Bepro-Travel/BePro-Web-API/wiki/Authentication-and-Setup)). Defaults to a Chrome desktop User-Agent if unset. |

Never hard-code these values into source you commit anywhere — pass them as environment variables, as shown below.

## Setup

```bash
export V23_BASE_URL="https://your_company.beprotravel.com"
export V23_CLIENT_USER="..."
export V23_CLIENT_PASS="..."

dotnet run
```

## Notes

- Targets **.NET 6+** (top-level statements, `System.Text.Json.Nodes`). For .NET Framework/older SDKs, swap in `Newtonsoft.Json.Linq.JObject` and `System.Net.Http.HttpClient` equivalents.
- `HotelInfo` and `Book` are `POST` with query-string parameters and **no body** — passed here as part of the URL, matching the raw HTTP contract (see [HotelInfo](https://github.com/Bepro-Travel/BePro-Web-API/wiki/HotelInfo) and [Book](https://github.com/Bepro-Travel/BePro-Web-API/wiki/Book)).
- Add your own retry/backoff and `sessionExpired` handling per [Conventions & Error Handling](https://github.com/Bepro-Travel/BePro-Web-API/wiki/Conventions-and-Error-Handling) before using this in production.

## Full API documentation

See the [BePro Web API wiki](https://github.com/Bepro-Travel/BePro-Web-API/wiki) for the full endpoint reference, request/response shapes, and conventions.
