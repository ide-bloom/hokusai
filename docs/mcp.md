# Generated MCP server

`src/<Name>.Mcp` is a stdio MCP server built on the official
[ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk). It shares the action classes and
`ActionRunner` with the REST API, so it validates and executes exactly the same way.

## Tools

One tool per action, named after it (`create_invoice`, `search_customers`, `delete_draft_invoice`):

- The **description** says whether the tool reads or writes, whether it is destructive, which form fields the inputs
  correspond to, whether it returns a CSV file, and whether it is disabled and why.
- Each **parameter** is described from its UI label and metadata ("'Customer'; a Customer Name, resolved to
  Customer.CustomerId (required)", "max 100 characters", "checkbox"). Line items are an array of objects whose fields
  are described the same way. Parameters for required inputs are required in the tool schema.
- **Annotations**: `readOnlyHint` for read actions, `destructiveHint` for actions that delete, `openWorldHint: false`.

## Writes need confirmation

Read tools run immediately. Write tools take two extra parameters:

- `confirm` (default `false`): without it the tool executes nothing and returns
  `{"error":"confirmation_required", "message": …}` explaining what would happen. Agents are expected to ask the user
  and call again with `confirm: true`.
- `dryRun` (default `false`): returns the planned statements without executing anything.

Invalid input returns `{"error":"invalid_input","errors":[…]}`; disabled actions return `{"error":"disabled", …}`.

## Running

```
dotnet user-secrets --project src/<Name>.Mcp set ConnectionStrings:Hokusai "<connection string>"
claude mcp add <name> -- dotnet run --project src/<Name>.Mcp
```

The server logs to stderr only (stdout carries the protocol), applies the same local-database guard as the API, and
refuses to start after schema drift. The end-to-end tests drive it with the SDK's client: they check that an
unconfirmed write changes nothing, that dry runs plan statements, and that confirmed calls reproduce the app's row
changes.
