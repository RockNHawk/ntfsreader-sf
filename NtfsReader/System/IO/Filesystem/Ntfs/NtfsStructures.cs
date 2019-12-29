using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.IO.Filesystem.Ntfs
{
    #region Ntfs Structures

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    unsafe struct BootSector
    {
        fixed byte AlignmentOrReserved1[3];
        public UInt64 Signature;
        public UInt16 BytesPerSector;
        public byte SectorsPerCluster;
        fixed byte AlignmentOrReserved2[26];
        public UInt64 TotalSectors;
        public UInt64 MftStartLcn;
        public UInt64 Mft2StartLcn;
        public UInt32 ClustersPerMftRecord;
        public UInt32 ClustersPerIndexRecord;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct VolumeData
    {
        public UInt64 VolumeSerialNumber;
        public UInt64 NumberSectors;
        public UInt64 TotalClusters;
        public UInt64 FreeClusters;
        public UInt64 TotalReserved;
        public UInt32 BytesPerSector;
        public UInt32 BytesPerCluster;
        public UInt32 BytesPerFileRecordSegment;
        public UInt32 ClustersPerFileRecordSegment;
        public UInt64 MftValidDataLength;
        public UInt64 MftStartLcn;
        public UInt64 Mft2StartLcn;
        public UInt64 MftZoneStart;
        public UInt64 MftZoneEnd;
    }

    enum RecordType : uint
    {
        File = 0x454c4946, //'FILE' in ASCII
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct RecordHeader
    {
        public RecordType Type; /* File type, for example 'FILE' */
        public UInt16 UsaOffset; /* Offset to the Update Sequence Array */
        public UInt16 UsaCount; /* Size in words of Update Sequence Array */
        public UInt64 Lsn; /* $LogFile Sequence Number (LSN) */
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct INodeReference
    {
        public UInt32 InodeNumberLowPart;
        public UInt16 InodeNumberHighPart;
        public UInt16 SequenceNumber;
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct FileRecordHeader
    {
        public RecordHeader RecordHeader;
        public UInt16 SequenceNumber; /* Sequence number */
        public UInt16 LinkCount; /* Hard link count */
        public UInt16 AttributeOffset; /* Offset to the first Attribute */
        public UInt16 Flags; /* Flags. bit 1 = in use, bit 2 = directory, bit 4 & 8 = unknown. */
        public UInt32 BytesInUse; /* Real size of the FILE record */
        public UInt32 BytesAllocated; /* Allocated size of the FILE record */
        public INodeReference BaseFileRecord; /* File reference to the base FILE record */
        public UInt16 NextAttributeNumber; /* Next Attribute Id */
        public UInt16 Padding; /* Align to 4 UCHAR boundary (XP) */
        public UInt32 MFTRecordNumber; /* Number of this MFT Record (XP) */
        public UInt16 UpdateSeqNum; /*  */
    };

    enum AttributeType : uint
    {
        AttributeInvalid = 0x00, /* Not defined by Windows */
        AttributeStandardInformation = 0x10,
        AttributeAttributeList = 0x20,
        AttributeFileName = 0x30,
        AttributeObjectId = 0x40,
        AttributeSecurityDescriptor = 0x50,
        AttributeVolumeName = 0x60,
        AttributeVolumeInformation = 0x70,
        AttributeData = 0x80,
        AttributeIndexRoot = 0x90,
        AttributeIndexAllocation = 0xA0,
        AttributeBitmap = 0xB0,
        AttributeReparsePoint = 0xC0, /* Reparse Point = Symbolic link */
        AttributeEAInformation = 0xD0,
        AttributeEA = 0xE0,
        AttributePropertySet = 0xF0,
        AttributeLoggedUtilityStream = 0x100
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Attribute
    {
        public AttributeType AttributeType;
        public UInt32 Length;
        public byte Nonresident;
        public byte NameLength;
        public UInt16 NameOffset;
        public UInt16 Flags; /* 0x0001 = Compressed, 0x4000 = Encrypted, 0x8000 = Sparse */
        public UInt16 AttributeNumber;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    unsafe struct AttributeList
    {
        public AttributeType AttributeType;
        public UInt16 Length;
        public byte NameLength;
        public byte NameOffset;
        public UInt64 LowestVcn;
        public INodeReference FileReferenceNumber;
        public UInt16 Instance;
        public fixed UInt16 AlignmentOrReserved[3];
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct AttributeFileName
    {
        public INodeReference ParentDirectory;
        public UInt64 CreationTime;
        public UInt64 ChangeTime;
        public UInt64 LastWriteTime;
        public UInt64 LastAccessTime;
        public UInt64 AllocatedSize;
        public UInt64 DataSize;
        public UInt32 FileAttributes;
        public UInt32 AlignmentOrReserved;
        public byte NameLength;
        public byte NameType; /* NTFS=0x01, DOS=0x02 */
        public char Name;
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct AttributeStandardInformation
    {
        public UInt64 CreationTime;
        public UInt64 FileChangeTime;
        public UInt64 MftChangeTime;
        public UInt64 LastAccessTime;

        public UInt32
            FileAttributes; /* READ_ONLY=0x01, HIDDEN=0x02, SYSTEM=0x04, VOLUME_ID=0x08, ARCHIVE=0x20, DEVICE=0x40 */

        public UInt32 MaximumVersions;
        public UInt32 VersionNumber;
        public UInt32 ClassId;
        public UInt32 OwnerId; // NTFS 3.0 only
        public UInt32 SecurityId; // NTFS 3.0 only
        public UInt64 QuotaCharge; // NTFS 3.0 only
        public UInt64 Usn; // NTFS 3.0 only
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct ResidentAttribute
    {
        public Attribute Attribute;
        public UInt32 ValueLength;
        public UInt16 ValueOffset;
        public UInt16 Flags; // 0x0001 = Indexed
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    unsafe struct NonResidentAttribute
    {
        public Attribute Attribute;
        public UInt64 StartingVcn;
        public UInt64 LastVcn;
        public UInt16 RunArrayOffset;
        public byte CompressionUnit;
        public fixed byte AlignmentOrReserved[5];
        public UInt64 AllocatedSize;
        public UInt64 DataSize;
        public UInt64 InitializedSize;
        public UInt64 CompressedSize; // Only when compressed
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
     struct NtfsFragment
    {
        public UInt64 Lcn; // Logical cluster number, location on disk.
        public UInt64 NextVcn; // Virtual cluster number of next fragment.

        public NtfsFragment(UInt64 lcn, UInt64 nextVcn)
        {
            Lcn = lcn;
            NextVcn = nextVcn;
        }
    }



    /*

 Layout of the Attribute
Offset	Size	Name	Description
0x00	16	GUID Object Id	Unique Id assigned to file
0x10	16	GUID Birth Volume Id	Volume where file was created
0x20	16	GUID Birth Object Id	Original Object Id of file
0x30	16	GUID Domain Id	Domain in which object was created

Birth Volume Id
Birth Volume Id is the Object Id of the Volume on which the Object Id was allocated. It never changes.

Birth Object Id
Birth Object Id is the first Object Id that was ever assigned to this MFT Record. I.e. If the Object Id is changed for some reason, this field will reflect the original value of the Object Id.

Domain Id
Domain Id is currently unused but it is intended to be used in a network environment where the local machine is part of a Windows 2000 Domain. This may be used in a Windows 2000 Advanced Server managed domain.

Notes
Other Information
This Attribute may be just 16 bytes long (the size of one GUID).

Even if the Birth Volume, Birth Object and Domain Ids are not used, they may be present, but one or more may be zero.

Need examples where all the fields are used.


https://flatcap.org/linux-ntfs/ntfs/attributes/object_id.html

     */
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    unsafe struct AttributeObjectId
    {
        //public fixed char FileId[16];
        public Guid ObjectId;
        public Guid BirthVolumeId;
        public Guid BirthObjectId;
        public Guid DomainId;

        ////[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        //public fixed char ObjectId[16];

    };

    #endregion
}