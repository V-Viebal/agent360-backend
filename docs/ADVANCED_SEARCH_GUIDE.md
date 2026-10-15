# Advanced Search with EF Core and Dynamic LINQ

`Hmz.Core.SharedKernel.AdvancedSearch` is the shared list-query pipeline for global search, field filters,
multi-key sorting, external-source matching, and pagination. It accepts a structured client contract and builds
Dynamic LINQ only from endpoint-owned, allow-listed field definitions.

The client never sends a raw predicate, property path, or Dynamic LINQ order string.

## Pipeline

```text
HTTP AdvancedSearchRequest
        ↓
IAdvancedSearchQuery<TResult>                 feature adapter
        ↓
IAdvancedSearchService                       validate + normalize + execute
        ↓
IAdvancedSearchExecutor / AdvancedSearchEngine
        ↓
IQueryable<TSource>                          Where → OrderBy → Count → Skip → Take
        ↓
Expression<Func<TSource, TResult>>           final DTO projection
        ↓
PagedResponse<TResult>
```

Applying search and ordering to `TSource` before the final response projection is important for EF Core provider
translation. It avoids filtering over a constructed DTO and keeps count, filtering, ordering, and pagination in SQL.

## Client request

The generated role and user samples are available at:

```http
POST /api/v1/roles/search
POST /api/v1/users/search
Content-Type: application/json
Authorization: Bearer <token>
```

The body is optional. An absent or empty JSON object requests page 1 with the endpoint defaults.

```json
{
  "search": [
    {
      "searchFields": ["Id", "Name", "Code"],
      "searchValue": "admin",
      "operator": "CT"
    }
  ],
  "searchLogic": "OR",
  "filter": {
    "logic": "AND",
    "filters": [
      {
        "searchField": "IsSystemRole",
        "searchValues": ["false"],
        "operator": "EQ"
      }
    ]
  },
  "orderBy": [
    { "field": "Name", "direction": "desc" },
    { "field": "Id", "direction": "asc" }
  ],
  "page": 1,
  "limit": 20
}
```

### Semantics

- Fields inside one `search` item are combined with **OR**.
- Separate search items use `searchLogic`; the default is **OR**.
- Filters use `filter.logic`; the default is **AND**.
- Multiple values in one positive filter (`EQ`, `CT`, `BW`, `EW`) are combined with **OR**.
- Multiple values in one negative filter (`NEQ`, `NCT`, `NBW`, `NEW`) are combined with **AND**.
- `orderBy` is evaluated in array order.
- The default order should end with a unique field to make pagination deterministic.
- `page` is one-based and defaults to `1`; `limit` defaults to `20`.

Canonical operator values written by the JSON converter are:

| Operator | Meaning |
|---|---|
| `EQ`, `NEQ` | equal / not equal |
| `LT`, `GT`, `LTE`, `GTE` | relational comparisons |
| `CT`, `NCT` | contains / not contains |
| `BW`, `NBW` | starts with / not starts with |
| `EW`, `NEW` | ends with / not ends with |
| `NULL`, `NNULL` | is null / is not null |

The converter also reads descriptive aliases such as `contains`, `notContains`, `startsWith`, `isNull`, and
`isNotNull`. Sorting accepts `asc` and `desc`.

### Scalar value formats

All filter values are JSON strings and are converted invariantly from the endpoint field configuration:

| Field type | Accepted request value |
|---|---|
| text | any configured-length string, for example `"admin"` |
| integer | base-10 integer, for example `"42"` |
| decimal | invariant decimal using `.`, for example `"19.95"` |
| boolean | `"true"` or `"false"` |
| date | ISO-8601 value, for example `"2026-01-31"` |
| enum | configured enum name, case-insensitive |

Values such as `"yes"` are intentionally rejected for booleans. The HTTP 400 validation response identifies the exact request path and lists the accepted format.

Delimiter formats such as `"Name|desc,Phone|asc"` are intentionally not part of the public contract. The object
array is easier to validate, document, and extend without parsing ambiguities.

## Role endpoint allow list

| Client field | Global search | Filter | Sort | Type |
|---|---:|---:|---:|---|
| `Id` | Yes; exact integer parse | Yes | Yes | `long` |
| `Name` | Yes | Yes | Yes | text |
| `Code` | Yes | Yes | Yes | text |
| `IsSystemRole` | No | Yes | Yes | boolean |

A numeric field can participate in global search even though the client supplies one string. The engine attempts an
invariant conversion for each field independently. For example, a term containing a valid role ID can match `Id`,
while a non-numeric term can still match `Name` or `Code` in the same OR group.

## User endpoint allow list

| Client field | Source member | Global search | Filter | Sort | Type |
|---|---|---:|---:|---:|---|
| `Id` | `Id` | Yes; exact integer parse | Yes | Yes | `long` |
| `ExternalUserId` | `LogtoUserId` | Yes | Yes | Yes | text |
| `Email` | `Email.Value` | Yes | Yes | Yes | text |
| `FirstName` | `FirstName` | Yes | Yes | Yes | text |
| `LastName` | `LastName` | Yes | Yes | Yes | text |
| `DisplayName` | `DisplayName` | Yes | Yes | Yes | text |

`MatchKey` keeps the public contract stable when the persistence member has a
different name or a value-object path. The query applies search, filtering,
ordering, count, and pagination to `User` in PostgreSQL. It then hydrates role
metadata only for users on the selected page. The `Roles` collection is
deliberately not an allow-listed Dynamic LINQ field.

## Feature implementation

### 1. Configure fields

`RoleAdvancedSearchConfiguration` declares everything the client may address:

```csharp
public static ISearchConfiguration Instance { get; } = new SearchConfiguration(
[
    new SearchFieldDefinition
    {
        Key = "Id",
        IsSearchable = true,
        IsFilterable = true,
        IsSortable = true,
        IsUnique = true,
        DataType = SearchDataType.Integer,
        ValueType = typeof(long),
        MaxDigits = 19
    },
    new SearchFieldDefinition
    {
        Key = "Name",
        IsSearchable = true,
        IsFilterable = true,
        IsSortable = true,
        DataType = SearchDataType.Text,
        GlobalSearchMode = GlobalSearchMode.TextContains,
        MaxValueLength = 100
    }
],
[
    new SearchOrder { Field = "Name", Direction = SortDirection.Asc },
    new SearchOrder { Field = "Id", Direction = SortDirection.Asc }
],
new AdvancedSearchLimits
{
    MaxPageSize = 100,
    MaxSearchItems = 5,
    MaxFieldsPerSearchItem = 4,
    MaxFilters = 10,
    MaxSortFields = 4
});
```

`Key` is the public client name. `MatchKey` is the member name on the query's source element type. Leave `MatchKey`
unset when both names are the same.

When a searchable value is computed, create a provider-translatable intermediate query shape and configure
`MatchKey` against that shape. Then use the projection overload to build the final response after paging:

```csharp
var source = db.Users.AsNoTracking().Select(user => new UserSearchRow
{
    Id = user.Id,
    Name = user.Name,
    Amount = user.Amount,
    AmountText = user.AmountTextColumn
});

return advancedSearch.ExecutePageAsync(
    source,
    row => new UserListItem(row.Id, row.Name, row.Amount),
    request,
    UserSearchConfiguration.Instance,
    cancellationToken: cancellationToken);
```

Do not introduce a non-translatable `ToString`, custom method, or client-side collection before the engine. Verify
provider SQL for computed columns and complex projections.

### 2. Implement the generic feature adapter

A feature does not need its own search-service interface. Implement the shared generic adapter contract:

```csharp
public sealed class RoleAdvancedSearchQuery(
    AppDbContext dbContext,
    IAdvancedSearchService advancedSearch)
    : IAdvancedSearchQuery<RoleResponse>
{
    public ValueTask<PagedResponse<RoleResponse>> ExecuteAsync(
        AdvancedSearchRequest request,
        CancellationToken cancellationToken = default) =>
        advancedSearch.ExecutePageAsync(
            dbContext.Roles.AsNoTracking(),
            role => new RoleResponse(
                role.Id,
                role.Name,
                role.Code,
                role.IsSystemRole,
                role.RowVersion),
            request,
            RoleAdvancedSearchConfiguration.Instance,
            cancellationToken: cancellationToken);
}
```

### 3. Keep the handler thin

```csharp
public sealed class SearchRolesQueryHandler(
    IAdvancedSearchQuery<RoleResponse> query)
    : IQueryHandler<SearchRolesQuery, PagedResponse<RoleResponse>>
{
    public ValueTask<PagedResponse<RoleResponse>> Handle(
        SearchRolesQuery request,
        CancellationToken cancellationToken) =>
        query.ExecuteAsync(request.Request, cancellationToken);
}
```

### 4. Register the shared services and adapter

```csharp
services.AddAdvancedSearch();
services.AddScoped<IAdvancedSearchQuery<RoleResponse>, RoleAdvancedSearchQuery>();
```

`AddAdvancedSearch` registers the stateless validator, executor, and `IAdvancedSearchService` façade. Feature
registration remains scoped because it depends on the scoped EF Core context.

## Validation and HTTP errors

Validation is performed before Dynamic LINQ parses any expression. Unknown fields, unsupported operators, invalid
scalar conversions, excessive values, unsafe page sizes, duplicate sort fields, and non-unique default ordering are
rejected.

`AdvancedSearchValidationException.Errors` uses JSON-style paths, for example:

```json
{
  "search[0].searchFields[1]": ["Field 'Secret' is not configured for global search."],
  "filter.filters[0].searchValues[0]": ["Value 'abc' is not valid for field 'Id'."],
  "orderBy[0].direction": ["Sort direction for field 'Name' is invalid. Accepted values: asc or desc."]
}
```

The endpoint maps this dictionary to RFC-compatible HTTP validation problem details with status `400`.

Malformed JSON, unknown operator strings, wrong JSON token types, and request-body binding failures are also converted by the shared `RequestBodyExceptionHandler` into sanitized HTTP 400 validation problem details. Ordinary binding failures use the stable `The request is invalid.` message; explicitly safe advanced-search converters expose bounded actionable messages, including accepted operator, search-logic, or sort-direction values. `AdvancedSearchRequest.SearchLogic`, `FilterGroup.Logic`, and `SearchOrder.Direction` apply their safe converters at the property boundary so they remain effective when the host also registers a catch-all `JsonStringEnumConverter`. Register `AddHmzProblemDetails()` and place `UseExceptionHandler()` before the endpoint pipeline.

## External-source fields

A configured field may represent display data owned by another service while the database stores only a local code.
Set `ExternalSourceKey` and point `MatchKey` at the local member:

```csharp
new SearchFieldDefinition
{
    Key = "Department",
    MatchKey = "DepartmentCode",
    IsSearchable = true,
    IsFilterable = true,
    ExternalSourceKey = "departments",
    ExternalFailurePolicy = ExternalFailurePolicy.MatchNothing
}
```

Supply resolvers to the service:

```csharp
var resolvers = new Dictionary<string, ExternalSearchResolver>(StringComparer.OrdinalIgnoreCase)
{
    ["departments"] = async (value, ct) =>
    {
        var codes = await departmentClient.FindCodesAsync(value, ct);
        return ExternalSearchResolution.Success(codes);
    }
};
```

Important behavior:

- Global external matches remain OR-ed inside their original search group.
- A successful lookup with no codes becomes match-nothing; it never falls through to an unfiltered query.
- External filters are rewritten to local match keys before the database query runs.
- `Unavailable` follows `ExternalFailurePolicy`: `MatchNothing`, `IgnoreCriterion`, or `RejectRequest`.
- External display-name enrichment belongs after pagination.

## Operational checklist

- Never expose raw Dynamic LINQ to the client.
- Keep the configuration endpoint-specific and review every exposed field.
- Set limits for page size, filters, search groups, values, digits, and text length.
- End default ordering with a unique key.
- Keep filtering, count, ordering, and pagination database-side.
- Use `AsNoTracking()` for read-only grids.
- Verify generated SQL with the production provider, not only EF Core InMemory.
- Prefer database columns or provider-translatable expressions for formatted search values.
- Cache repeated external lookups and propagate cancellation tokens.

The template test suite includes both behavior tests using EF Core InMemory and a PostgreSQL `ToQueryString()`
translation test that does not require a running database server.
