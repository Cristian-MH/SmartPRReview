# SmartPRReview

AI-assisted pull request review. The backend is an ASP.NET Core API and receives
the repository to review as part of every request. Review state is ephemeral and
stored in an in-memory cache; no database is required.

## Run the backend

```powershell
dotnet run --project backend/SmartPRReview.Api
```

Open `/swagger` in the browser to explore and test the available endpoints.

Create a review:

```http
POST /api/reviews
Content-Type: application/json

{
  "provider": "GitHub",
  "location": "https://github.com/example/project.git",
  "pullRequestNumber": 42,
  "baseReference": "main",
  "headReference": "feature/example"
}
```

The API returns `202 Accepted` with a review ID. Read its current state using
`GET /api/reviews/{id}`. Cached reviews expire after 24 hours by default.

For a private GitHub repository, configure a token before starting the API:

```powershell
$env:GitHub__Token = "your-token"
dotnet run --project backend/SmartPRReview.Api
```

The token must have read access to the repository and pull requests. It is read
from configuration and is never accepted in the review request or returned by
the API.
