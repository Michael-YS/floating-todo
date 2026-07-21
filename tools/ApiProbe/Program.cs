using System.Reflection;
using Ical.Net;

var assembly = typeof(Calendar).Assembly;
foreach (var type in assembly.GetExportedTypes()
             .Where(type => type.FullName is not null &&
                            (type.FullName.Contains("Todo") ||
                             type.Name is "Calendar" or "CalendarSerializer" or "RecurrencePattern" or "RecurrenceRule" or "Occurrence" or "Period" or "EvaluationOptions" or "CalDateTime"))
             .OrderBy(type => type.FullName))
{
    Console.WriteLine($"TYPE {type.FullName}");
    foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                 .Where(member => member.MemberType is MemberTypes.Method or MemberTypes.Property or MemberTypes.Constructor)
                 .OrderBy(member => member.Name))
    {
        Console.WriteLine($"  {member}");
    }
}

Console.WriteLine("TODO INHERITED PROPERTIES");
foreach (var property in typeof(Ical.Net.CalendarComponents.Todo).GetProperties().OrderBy(property => property.Name))
{
    Console.WriteLine($"  {property.PropertyType} {property.Name}");
}

var recurrenceIdentifierType = typeof(Ical.Net.CalendarComponents.Todo).GetProperty("RecurrenceIdentifier")!.PropertyType;
Console.WriteLine($"RECURRENCE IDENTIFIER {recurrenceIdentifierType.FullName}");
foreach (var member in recurrenceIdentifierType.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
{
    Console.WriteLine($"  {member}");
}
