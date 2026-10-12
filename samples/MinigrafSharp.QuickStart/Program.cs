using MinigrafSharp;

using var db = MinigrafDatabase.OpenMemory();

string result = db.ExecuteRaw(
    """(transact [[:alice :name "Alice"]])""");

Console.WriteLine(result);
