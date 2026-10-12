using MinigrafSharp;

string? overridePath = Environment.GetEnvironmentVariable("MINIGRAFSHARP_NATIVE_PATH");
Console.WriteLine("override=" + (string.IsNullOrEmpty(overridePath) ? "absent" : overridePath));

using var db = MinigrafDatabase.OpenMemory();

db.Execute("""(transact [[:alice :name "Alice"]])""");

var result = (MinigrafQueryResult)db.Execute(
    """(query [:find ?name :where [?e :name ?name]])""");

Console.WriteLine(result.Rows[0]["?name"].GetString());
