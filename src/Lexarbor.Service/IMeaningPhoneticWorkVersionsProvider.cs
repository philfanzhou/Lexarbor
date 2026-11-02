using Lexarbor.Domain.Repositories;

namespace Lexarbor.Service;

public interface IMeaningPhoneticWorkVersionsProvider
{
    MeaningPhoneticWorkVersions Capture();
}
