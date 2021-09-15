﻿using System;

namespace SharpHoundCommonLib.Enums
{
    // from https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-crtd/1192823c-d839-4bc3-9b6b-fa8c53507ae1
    // and from certutil.exe -v -dstemplate
    [Flags]
    public enum PKICertificateNameFlag : uint
    {
        ENROLLEE_SUPPLIES_SUBJECT = 0x00000001,
        ADD_EMAIL = 0x00000002,
        ADD_OBJ_GUID = 0x00000004,
        OLD_CERT_SUPPLIES_SUBJECT_AND_ALT_NAME = 0x00000008,
        ADD_DIRECTORY_PATH = 0x00000100,
        ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME = 0x00010000,
        SUBJECT_ALT_REQUIRE_DOMAIN_DNS = 0x00400000,
        SUBJECT_ALT_REQUIRE_SPN = 0x00800000,
        SUBJECT_ALT_REQUIRE_DIRECTORY_GUID = 0x01000000,
        SUBJECT_ALT_REQUIRE_UPN = 0x02000000,
        SUBJECT_ALT_REQUIRE_EMAIL = 0x04000000,
        SUBJECT_ALT_REQUIRE_DNS = 0x08000000,
        SUBJECT_REQUIRE_DNS_AS_CN = 0x10000000,
        SUBJECT_REQUIRE_EMAIL = 0x20000000,
        SUBJECT_REQUIRE_COMMON_NAME = 0x40000000,
        SUBJECT_REQUIRE_DIRECTORY_PATH = 0x80000000,
    }
}