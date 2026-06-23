using System;
using System.Collections.Generic;
using System.Text;

namespace Dotsy.Core.Session.Data;

public enum SessionRecordType
{
    None,
    User,
    Assistant,
    ToolResult,
    Summary,
    End
}
