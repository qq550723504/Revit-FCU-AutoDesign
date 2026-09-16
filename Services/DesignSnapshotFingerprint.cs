using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FCUAutoDesign
{
    internal static class DesignSnapshotFingerprint
    {
        public static string Compute(DesignSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            IEnumerable<string> roomParts = snapshot.Rooms.OrderBy(x => x.RoomUniqueId, StringComparer.Ordinal)
                .Select(x => string.Join("|", x.RoomUniqueId, Number(x.Length), Number(x.Height),
                    Number(x.CoolingLoadDensity), Number(x.DesignCoolingLoad), x.UnitCount,
                    Join(x.DeviceLogicalIds), Join(x.PipeLogicalIds)));
            IEnumerable<string> deviceParts = snapshot.Devices.OrderBy(x => x.LogicalDeviceId, StringComparer.Ordinal)
                .Select(x => string.Join("|", x.LogicalDeviceId, x.ElementUniqueId, x.FamilySymbolUniqueId,
                    Point(x.Position), x.Orientation, (int)x.State));
            IEnumerable<string> pipeParts = snapshot.Pipes.OrderBy(x => x.LogicalPipeId, StringComparer.Ordinal)
                .Select(x => string.Join("|", x.LogicalPipeId, x.ElementUniqueId, (int)x.Role,
                    (int)x.Ownership, x.SharedNetworkId, Number(x.CurrentDiameter), Number(x.PlannedDiameter),
                    (int)x.State, Join(x.ServiceRoomUniqueIds), Join(x.ConnectedElementUniqueIds)));
            string canonical = string.Join("\n", roomParts.Concat(deviceParts).Concat(pipeParts));
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string Number(DesignQuantity value) => value == null ? string.Empty
            : ((int)value.Unit) + ":" + value.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        private static string Point(DesignPoint value) => value == null ? string.Empty
            : string.Join(":", (int)value.Unit, value.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                value.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                value.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture), value.CoordinateReference);
        private static string Join<T>(IEnumerable<T> values) => values == null ? string.Empty
            : string.Join(",", values.Select(x => x == null ? string.Empty : x.ToString()).OrderBy(x => x, StringComparer.Ordinal));
    }
}
