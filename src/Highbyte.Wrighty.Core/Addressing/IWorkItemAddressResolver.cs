using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Addressing;

public interface IWorkItemAddressResolver
{
    WorkItemId Resolve(string input, TrackerConfig config);

    string FormatShort(WorkItemId id, TrackerConfig config);
}
