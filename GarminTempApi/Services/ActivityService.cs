using System.Threading.Tasks;
using GarminTempApi.Data;
using GarminTempApi.Models;

namespace GarminTempApi.Services
{
    public class ActivityService
    {
        private readonly AppDbContext _db;
        public ActivityService(AppDbContext db) => _db = db;

        public async Task SaveActivityAsync(Activity activity)
        {
            _db.Activities.Add(activity);
            await _db.SaveChangesAsync();
        }
    }
}
