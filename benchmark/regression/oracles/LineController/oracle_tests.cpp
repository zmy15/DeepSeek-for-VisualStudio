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
#if defined(ORACLE_CASE_CPP_PE_REGULAR)
    else if (caseId == "cpp-pe-regular")
    {
        checks.emplace_back("GetPage returns one-based pages", [&] {
            AssertEqual("GetPage(0, 5)", 1, counter.GetPage(0, 5));
            AssertEqual("GetPage(4, 5)", 1, counter.GetPage(4, 5));
            AssertEqual("GetPage(5, 5)", 2, counter.GetPage(5, 5));
        });
        checks.emplace_back("GetPage validates parameters", [&] {
            bool negativeRejected = false;
            try
            {
                (void)counter.GetPage(-1, 5);
            }
            catch (const std::invalid_argument&)
            {
                negativeRejected = true;
            }
            AssertEqual("negative item index", true, negativeRejected);

            bool zeroPageSizeRejected = false;
            try
            {
                (void)counter.GetPage(0, 0);
            }
            catch (const std::invalid_argument&)
            {
                zeroPageSizeRejected = true;
            }
            AssertEqual("zero page size", true, zeroPageSizeRejected);
        });
    }
#endif
#if defined(ORACLE_CASE_CPP_CQ_BOUNDARY)
    else if (caseId == "cpp-cq-boundary")
    {
        checks.emplace_back("FirstItemIndex keeps ordinary behavior", [&] {
            AssertEqual("FirstItemIndex(1, 5)", 0, counter.FirstItemIndex(1, 5));
            AssertEqual("FirstItemIndex(3, 5)", 10, counter.FirstItemIndex(3, 5));
        });
        checks.emplace_back("FirstItemIndex rejects multiplication overflow", [&] {
            bool overflowRejected = false;
            try
            {
                (void)counter.FirstItemIndex(
                    std::numeric_limits<int>::max(),
                    2);
            }
            catch (const std::overflow_error&)
            {
                overflowRejected = true;
            }
            AssertEqual("INT_MAX * 2", true, overflowRejected);
        });
    }
#endif
#if defined(ORACLE_CASE_CPP_SP_REGULAR)
    else if (caseId == "cpp-sp-regular")
    {
        checks.emplace_back("ShouldAlarm includes 80", [&] {
            AssertEqual("ShouldAlarm(80)", true, policy.ShouldAlarm(80.0));
        });
        checks.emplace_back("ShouldAlarm keeps 79.99 clear", [&] {
            AssertEqual("ShouldAlarm(79.99)", false, policy.ShouldAlarm(79.99));
        });
    }
#endif
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
