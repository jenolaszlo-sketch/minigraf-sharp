using MinigrafSharp;

using var db = MinigrafDatabase.OpenMemory();

db.Execute(
    """(transact [[:alice :name "Alice"]])""");

var result = (MinigrafQueryResult)db.Execute(
    """(query [:find ?name :where [?e :name ?name]])""");

Console.WriteLine(
    result.Rows[0]["?name"].GetString());
