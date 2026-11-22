#pragma once

#include <stdexcept>

namespace line_controller
{
    enum class AlarmLevel
    {
        Normal,
        Warning,
        Critical
    };

    struct AlarmInput
    {
        double temperatureCelsius;
        double vibrationMm;
        bool doorOpen;
        bool maintenance;
    };

    class AlarmPolicy
    {
    public:
        AlarmLevel Evaluate(const AlarmInput& input) const;
        bool ShouldAlarm(double temperatureCelsius) const;
    };

    class BatchCounter
    {
    public:
        int PageCount(int totalItems, int pageSize) const;
        int FirstItemIndex(int page, int pageSize) const;
    };
}
