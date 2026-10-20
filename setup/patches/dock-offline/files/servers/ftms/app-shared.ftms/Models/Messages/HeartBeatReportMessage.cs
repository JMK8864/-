using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ftms.shared.Model.Messages;

public class HeartBeatReportMessage : TcpMessageRequestBase
{
    #region [ class Device ]
    public class DeviceStatus
    {
        public string Id { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    }
    #endregion

    [JsonPropertyOrder(-900)]
    public string? Version { get; set; } 

    public required IReadOnlyCollection<DeviceStatus> DeviceStatuses { get; init; }

    public DockSortingActiveParameter? DockSortingParam { get; set; }    //public DockSetting? DockSetting { get; set; }

    // [도크 끊김 판정] 서버가 heartbeat 를 받은 시각 (서버 시계 기준, 도크 PC 시계와 무관)
    public DateTimeOffset? ReceivedAt { get; set; }

    // [도크 끊김 판정] 서버가 TCP 연결 종료를 감지하면 false. null 이면 정보 없음(이전 버전 데이터)
    public bool? Connected { get; set; }

    // [도크 끊김 판정] 서버가 TCP 연결 종료를 감지한 시각
    public DateTimeOffset? DisconnectedAt { get; set; }
}

public class HeartBeatResponse : TcpMessageResponseBase
{
    public DockSortingActiveParameter? SetParameter { get; set; }    //public DockSetting? DockSetting { get; set; }
}


public static class HeartBeatSerializer
{
    public static string ToJson(this IEnumerable<HeartBeatReportMessage> reports)
    {
        var now = DateTimeOffset.UtcNow;

        // 1. 루트 배열 생성 [공통시간, ISO시간, 도크배열]
        var rootArray = new List<object>
        {
            now.ToUnixTimeMilliseconds(),   // [0] 공통 Timestamp
            now.ToString("o"),              // [1] ISO 8601 형식 시간
            SerializeDocks(reports)         // [2] 도크 데이터 리스트
        };

        return JsonSerializer.Serialize(rootArray);
    }

    private static List<object> SerializeDocks(IEnumerable<HeartBeatReportMessage> reports)
    {
        var dockList = new List<object>();

        foreach (var report in reports)
        {
            // 도크별 장치 순서 고정 (중요: 키가 없으므로 순서가 곧 이름임)
            var deviceIds = new[] { "scanner", "dimensioner", "sensor", "conveyor" };
            var deviceDataList = new List<object>();

            foreach (var id in deviceIds)
            {
                var device = report.DeviceStatuses.FirstOrDefault(d => d.Id == id);
                if (device != null)
                {
                    int[] arry;
                    //try { arry = ExtractHexToNumbers(device.Status); }
                    try { arry = ExtractHexToNumbers_v2(device.Status); }
                    catch (Exception) { arry = new int[] { }; }

                    deviceDataList.Add(new object[]
                    {
                        device.LastSeenAt.ToUnixTimeMilliseconds(),     // [0] LastSeen (long)
                        arry,                                           // [1] Status (int[])
                    });
                }
                else
                {
                    deviceDataList.Add(null); // 장치가 없을 경우 인덱스 유지를 위해 null
                }
            }

            // 도크 정렬 파라미터 처리
            object? sortingParam = report.DockSortingParam != null
                ? new object[] { report.DockSortingParam.DockId, report.DockSortingParam.ParamName, report.DockSortingParam.UpdatedAt.ToUnixTimeMilliseconds() }
                : null;

            // 개별 도크 배열 구성 ["B23", [장치들], 파라미터, 서버수신시각, 연결여부]
            //   [3], [4] 는 도크 끊김 판정용으로 추가 (뒤에 붙였으므로 기존 클라이언트는 영향 없음)
            dockList.Add(new object?[]
            {
                report.DockSortingParam?.DockId ?? "Unknown", // ClientId 대용
                deviceDataList,
                sortingParam,
                report.ReceivedAt?.ToUnixTimeMilliseconds(),  // [3] 서버 수신 시각(ms), 없으면 null
                report.Connected,                             // [4] true / false / null
            });
        }

        return dockList;
    }

    public static int[] ExtractHexToNumbers(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Array.Empty<int>();

        var results = new List<int>(32);

        // 2글자씩 순차 분할
        var length = input.Length - (input.Length % 2);
        for (var i = 0; i < length; i += 2)
        {
            var token = input.Substring(i, 2);

            // 각 토큰을 개별적으로 16진수로 파싱 시도
            // 유효하지 않으면 건너뜀 (예: "CS")
            if (int.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                // 00~FF 범위만 추가
                if (value >= 0 && value <= 255)
                    results.Add(value);
            }
            // else: 무시
        }

        return results.ToArray();
    }


    public static int[] ExtractHexToNumbers_v2(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Array.Empty<int>();

        var results = new List<int>(32);

        // 쉼표로 구분된 토큰들을 분리
        var tokens = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            // 각 토큰을 16진수로 파싱 시도
            if (int.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                // 00~FF 범위만 추가 (선택적, 더 큰 값도 허용하려면 제거)
                //if (value >= 0 && value <= 255)
                results.Add(value);
            }
            // else: 유효하지 않은 토큰은 무시
        }

        return results.ToArray();
    }
}
