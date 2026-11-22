#include "line_controller/alarm_policy.h"

#include <limits>

namespace line_controller
{
    int BatchCounter::PageCount(int totalItems, int pageSize) const
    {
        if (totalItems < 0)
        {
            throw std::invalid_argument("totalItems must be non-negative");
        }
        if (pageSize <= 0)
        {
            throw std::invalid_argument("pageSize must be positive");
        }
        if (totalItems == 0)
        {
            return 0;
        }

        return (totalItems + pageSize - 1) / pageSize;
    }

    int BatchCounter::FirstItemIndex(int page, int pageSize) const
    {
        if (page <= 0)
        {
            throw std::invalid_argument("page must be positive");
        }
        if (pageSize <= 0)
        {
            throw std::invalid_argument("pageSize must be positive");
        }

        return (page - 1) * pageSize;
    }
}
