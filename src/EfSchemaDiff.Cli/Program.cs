using System.CommandLine;
using EfSchemaDiff.Cli.Commands;

var root = new RootCommand("ef-schema-diff — detect schema drift between EF Core model and SQL Server database.")
{
    CompareCommand.Build()
};

return await root.InvokeAsync(args);
