using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GarminTempApi.Models;

namespace GarminTempApi.Services
{
    public class FitActivityParser
    {
        public FitActivityParser()
        {
        }

        public async Task<List<Activity>> ParseAsync(Stream fileStream, string fileName)
        {
            // TODO: Implement using Garmin FIT SDK Decode API.
            throw new NotImplementedException("FIT parser scaffold - implement using Garmin FIT SDK.");
        }
    }
}
