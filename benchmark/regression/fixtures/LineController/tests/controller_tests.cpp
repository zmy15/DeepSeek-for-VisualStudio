#include "line_controller/alarm_policy.h"

#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
    using line_controller::AlarmInput;
    using line_controller::AlarmLevel;
    using line_controller::AlarmPolicy;
    using line_controller::BatchCounter;

    void AssertEqual(const std::string& name, int expected, int actual)
    {
        if (expected != actual)
        {
            throw std::runtime_error(
                name + ": expected " + std::to_string(expected)
                + ", got " + std::to_string(actual));
        }
    }

    void AssertEqual(const std::string& name, AlarmLevel expected, AlarmLevel actual)
    {
        if (expected != actual)
        {
            throw std::runtime_error(name + ": alarm level mismatch");
        }
    }

    void AssertEqual(const std::string& name, bool expected, bool actual)
    {
        if (expected != actual)
        {
            throw std::runtime_error(
                name + ": expected " + (expected ? "true" : "false")
                + ", got " + (actual ? "true" : "false"));
        }
    }
}

int main()
{
    std::vector<std::pair<std::string, std::function<void()>>> checks;
    AlarmPolicy policy;
    BatchCounter counter;

    checks.emplace_back("PageCount returns zero for no items", [&] {
        AssertEqual("PageCount(0, 5)", 0, counter.PageCount(0, 5));
    });
    checks.emplace_back("PageCount returns exact pages", [&] {
        AssertEqual("PageCount(10, 5)", 2, counter.PageCount(10, 5));
    });
    checks.emplace_back("PageCount rounds partial pages up", [&] {
        AssertEqual("PageCount(11, 5)", 3, counter.PageCount(11, 5));
    });
    checks.emplace_back("Maintenance suppresses alarms", [&] {
        AlarmInput input{100.0, 10.0, true, true};
        AssertEqual("maintenance", AlarmLevel::Normal, policy.Evaluate(input));
    });
    checks.emplace_back("Open hot door is critical", [&] {
        AlarmInput input{80.0, 0.0, true, false};
        AssertEqual("open hot door", AlarmLevel::Critical, policy.Evaluate(input));
    });
    checks.emplace_back("High vibration is critical", [&] {
        AlarmInput input{20.0, 8.0, false, false};
        AssertEqual("high vibration", AlarmLevel::Critical, policy.Evaluate(input));
    });
    checks.emplace_back("Threshold temperature is warning", [&] {
        AlarmInput input{80.0, 0.0, false, false};
        AssertEqual("temperature threshold", AlarmLevel::Warning, policy.Evaluate(input));
    });
    checks.emplace_back("ShouldAlarm remains exclusive at 80", [&] {
        AssertEqual("ShouldAlarm(80)", false, policy.ShouldAlarm(80.0));
        AssertEqual("ShouldAlarm(81)", true, policy.ShouldAlarm(81.0));
    });

    int passed = 0;
    for (const auto& check : checks)
    {
        try
        {
            check.second();
            ++passed;
            std::cout << "PASS " << check.first << '\n';
        }
        catch (const std::exception& ex)
        {
            std::cerr << "FAIL " << check.first << ": " << ex.what() << '\n';
        }
    }

    std::cout << passed << "/" << checks.size() << " checks passed\n";
    return passed == static_cast<int>(checks.size()) ? 0 : 1;
}
