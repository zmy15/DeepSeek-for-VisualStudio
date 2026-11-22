#include "line_controller/alarm_policy.h"

#include <functional>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
    using line_controller::AlarmPolicy;
    using line_controller::BatchCounter;

    void AssertEqual(const std::string& name, bool expected, bool actual)
    {
        if (expected != actual)
        {
            throw std::runtime_error(
                name + ": expected " + (expected ? "true" : "false")
                + ", got " + (actual ? "true" : "false"));
        }
    }

    void AssertEqual(const std::string& name, int expected, int actual)
    {
        if (expected != actual)
        {
            throw std::runtime_error(
                name + ": expected " + std::to_string(expected)
                + ", got " + std::to_string(actual));
        }
    }
}

int main(int argc, char** argv)
{
    if (argc != 2)
    {
        std::cerr << "usage: line_controller_oracle_tests <case-id>\n";
        return 2;
    }

    const std::string caseId = argv[1];
    std::vector<std::pair<std::string, std::function<void()>>> checks;
    AlarmPolicy policy;
    BatchCounter counter;

    if (caseId == "cpp-if-regular")
    {
        checks.emplace_back("79.99 is below threshold", [&] {
            AssertEqual("ShouldAlarm(79.99)", false, policy.ShouldAlarm(79.99));
        });
        checks.emplace_back("80 is inclusive", [&] {
            AssertEqual("ShouldAlarm(80)", true, policy.ShouldAlarm(80.0));
        });
        checks.emplace_back("81 remains alarming", [&] {
            AssertEqual("ShouldAlarm(81)", true, policy.ShouldAlarm(81.0));
        });
    }
    else if (caseId == "cpp-cq-regular")
    {
        checks.emplace_back("ordinary page counts remain stable", [&] {
            AssertEqual("PageCount(0, 5)", 0, counter.PageCount(0, 5));
            AssertEqual("PageCount(10, 5)", 2, counter.PageCount(10, 5));
            AssertEqual("PageCount(11, 5)", 3, counter.PageCount(11, 5));
        });
        checks.emplace_back("max int does not overflow", [&] {
            AssertEqual(
                "PageCount(max, 5)",
                429496730,
                counter.PageCount(std::numeric_limits<int>::max(), 5));
        });
    }
    else
    {
        std::cerr << "unknown LineController oracle case: " << caseId << '\n';
        return 2;
    }

    int failed = 0;
    for (const auto& check : checks)
    {
        try
        {
            check.second();
            std::cout << "PASS " << check.first << '\n';
        }
        catch (const std::exception& ex)
        {
            ++failed;
            std::cerr << "FAIL " << check.first << ": " << ex.what() << '\n';
        }
    }

    std::cout << checks.size() - failed << "/" << checks.size()
              << " oracle checks passed\n";
    return failed == 0 ? 0 : 1;
}
