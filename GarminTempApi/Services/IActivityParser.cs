using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using GarminTempApi.Models;

namespace GarminTempApi.Services
{
    public interface IActivityParser
    {
        Task<List<Activity>> ParseAsync(Stream fileStream, string fileName);
    }
}
