using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GatherBuddy.Classes;
using GatherBuddy.Models;
using GatherBuddy.Utilities;
using Lumina.Data.Parsing;
using Newtonsoft.Json;

namespace GatherBuddy.FishTimer;

public partial class FishRecorder
{
    public const  string                  RemoteFishRecordsFileName = "GatherBuddy.CustomInfo.fish_records.json";
    internal      List<FishRecord>        RemoteRecords             = [];

    private readonly Random _random = new(Guid.NewGuid().GetHashCode());
    public (Vector3 Position, Angle Rotation)? GetPositionForFishingSpot(FishingSpot spot)
    {
        var allValidRecords = RemoteRecords.Union(Records).Where(r => r.FishingSpot == spot && r.PositionDataValid);
        if (!allValidRecords.Any())
            return null;

        var random = _random.Next(0, allValidRecords.Count());
        var selectedRecord = allValidRecords.ElementAt(random);
        return (selectedRecord.Position, selectedRecord.RotationAngle);
    }

    public (Vector3 Position, Angle Rotation)? GetPositionForFishingSpot(FishingSpot spot, Vector3 avoidPosition, float minDistance)
    {
        var allValidRecords = RemoteRecords.Union(Records).Where(r => r.FishingSpot == spot && r.PositionDataValid).ToList();
        if (allValidRecords.Count == 0)
            return null;

        var farEnough = allValidRecords
            .Where(r => Vector3.Distance(r.Position, avoidPosition) >= minDistance)
            .ToList();

        FishRecord selectedRecord;
        if (farEnough.Count > 0)
        {
            var random = _random.Next(0, farEnough.Count);
            selectedRecord = farEnough[random];
        }
        else
        {
            selectedRecord = allValidRecords
                .OrderByDescending(r => Vector3.Distance(r.Position, avoidPosition))
                .First();
        }

        return (selectedRecord.Position, selectedRecord.RotationAngle);
    }

    private void LoadRemoteFile()
    {
        try
        {
            var embeddedResource = typeof(FishRecorder).Assembly.GetManifestResourceStream(RemoteFishRecordsFileName);
            if (embeddedResource == null)
                throw new FileNotFoundException($"Could not find embedded resource {RemoteFishRecordsFileName}");
            using var reader = new StreamReader(embeddedResource);
            var       json   = reader.ReadToEnd();
            var       records = JsonConvert.DeserializeObject<List<SimpleFishRecord>>(json);
            if (records == null)
                throw new JsonException("Could not deserialize remote fish records.");

            foreach (var record in records)
            {
                var fishRecord = FishRecord.FromSimpleRecord(record);
                RemoteRecords.Add(fishRecord);
            }
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Could not read fish record file {FishRecordFileName}:\n{e}");
        }
    }

}
