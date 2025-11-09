using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using GarminTempApi.Models;

namespace GarminTempApi.Services
{
    public class GpxTcxParser : IActivityParser
    {
        public async Task<List<Activity>> ParseAsync(Stream fileStream, string fileName)
        {
            fileStream.Position = 0;
            using var sr = new StreamReader(fileStream);
            var xml = await sr.ReadToEndAsync();
            var doc = XDocument.Parse(xml);

            var rootName = doc.Root?.Name.LocalName?.ToLowerInvariant() ?? string.Empty;
            if (rootName.Contains("gpx"))
                return ParseGpx(doc, fileName);
            if (rootName.Contains("tcx") || doc.Descendants().Any(x => x.Name.LocalName == "TrainingCenterDatabase"))
                return ParseTcx(doc, fileName);

            throw new InvalidOperationException("Unsupported XML file (not GPX or TCX)");
        }

        private List<Activity> ParseGpx(XDocument doc, string fileName)
        {
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var trkpts = doc.Descendants(ns + "trkpt").ToList();

            if (!trkpts.Any()) return new List<Activity>();

            var activity = new Activity
            {
                FileName = fileName,
                Source = "upload",
                ActivityType = "Unknown"
            };

            foreach (var pt in trkpts)
            {
                var lat = double.Parse(pt.Attribute("lat").Value, System.Globalization.CultureInfo.InvariantCulture);
                var lon = double.Parse(pt.Attribute("lon").Value, System.Globalization.CultureInfo.InvariantCulture);
                var timeNode = pt.Element(ns + "time");
                DateTime ts = timeNode != null ? DateTime.Parse(timeNode.Value, null, System.Globalization.DateTimeStyles.AssumeUniversal) : DateTime.UtcNow;

                var eleNode = pt.Element(ns + "ele");
                double? ele = eleNode != null ? double.Parse(eleNode.Value, System.Globalization.CultureInfo.InvariantCulture) : (double?)null;

                var hrNode = pt.Descendants().FirstOrDefault(e => e.Name.LocalName == "hr" || e.Name.LocalName == "heart_rate");
                double? hr = null;
                if (hrNode != null && double.TryParse(hrNode.Value, out var hrv)) hr = hrv;

                activity.Points.Add(new ActivityPoint
                {
                    Timestamp = ts,
                    Latitude = lat,
                    Longitude = lon,
                    Altitude = ele,
                    HeartRate = hr
                });
            }

            activity.StartTime = activity.Points.First().Timestamp;
            activity.Duration = activity.Points.Last().Timestamp - activity.Points.First().Timestamp;
            return new List<Activity> { activity };
        }

        private List<Activity> ParseTcx(XDocument doc, string fileName)
        {
            var trackpoints = doc.Descendants().Where(x => x.Name.LocalName == "Trackpoint").ToList();
            if (!trackpoints.Any()) return new List<Activity>();

            var activity = new Activity
            {
                FileName = fileName,
                Source = "upload",
                ActivityType = "Unknown"
            };

            foreach (var tp in trackpoints)
            {
                var timeNode = tp.Elements().FirstOrDefault(e => e.Name.LocalName == "Time");
                DateTime ts = timeNode != null ? DateTime.Parse(timeNode.Value, null, System.Globalization.DateTimeStyles.AssumeUniversal) : DateTime.UtcNow;

                var pos = tp.Elements().FirstOrDefault(e => e.Name.LocalName == "Position");
                double lat = 0, lon = 0;
                if (pos != null)
                {
                    var latNode = pos.Elements().FirstOrDefault(e => e.Name.LocalName == "LatitudeDegrees");
                    var lonNode = pos.Elements().FirstOrDefault(e => e.Name.LocalName == "LongitudeDegrees");
                    if (latNode != null) double.TryParse(latNode.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lat);
                    if (lonNode != null) double.TryParse(lonNode.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lon);
                }

                var eleNode = tp.Elements().FirstOrDefault(e => e.Name.LocalName == "AltitudeMeters");
                double? ele = null;
                if (eleNode != null && double.TryParse(eleNode.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ev)) ele = ev;

                var hrNode = tp.Descendants().FirstOrDefault(e => e.Name.LocalName == "Value" && e.Parent?.Name.LocalName == "HeartRateBpm");
                double? hr = null;
                if (hrNode != null && double.TryParse(hrNode.Value, out var hv)) hr = hv;

                activity.Points.Add(new ActivityPoint
                {
                    Timestamp = ts,
                    Latitude = lat,
                    Longitude = lon,
                    Altitude = ele,
                    HeartRate = hr
                });
            }

            activity.StartTime = activity.Points.First().Timestamp;
            activity.Duration = activity.Points.Last().Timestamp - activity.Points.First().Timestamp;
            return new List<Activity> { activity };
        }
    }
}
