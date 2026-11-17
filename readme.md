run:
dotnet C:\\dev\\sticky-todo\\bin\\Debug\\net10.0-windows\\StickyTodo.dll

run in rider:
In Rider, open Run → Edit Configurations, add a .NET Executable configuration, and set:
Executable: C:\\Program Files\\dotnet\\dotnet.exe
Program arguments: C:\\dev\\sticky-todo\\bin\\Debug\\net10.0-windows\\StickyTodo.dll
Working directory: C:\\dev\\sticky-todo\\bin\\Debug\\net10.0-windows
Before launch: build StickyTodo