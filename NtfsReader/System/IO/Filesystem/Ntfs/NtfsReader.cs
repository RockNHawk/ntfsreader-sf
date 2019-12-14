/*
    The NtfsReader library.

    Copyright (C) 2008 Danny Couture

    This library is free software; you can redistribute it and/or
    modify it under the terms of the GNU Lesser General Public
    License as published by the Free Software Foundation; either
    version 2.1 of the License, or (at your option) any later version.

    This library is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
    Lesser General Public License for more details.

    You should have received a copy of the GNU Lesser General Public
    License along with this library; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
  
    For the full text of the license see the "License.txt" file.

    This library is based on the work of Jeroen Kessels, Author of JkDefrag.
    http://www.kessels.com/Jkdefrag/
    
    Special thanks goes to him.
  
    Danny Couture
    Software Architect
    mailto:zerk666@gmail.com
*/
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.IO.Filesystem.Ntfs
{


    internal sealed partial class NtfsReader : IDisposable
    {

        #region Constants

        private const UInt64 VIRTUALFRAGMENT = 18446744073709551615; // _UI64_MAX - 1 */
        private const UInt32 ROOTDIRECTORY = 5;

        private readonly byte[] BitmapMasks = new byte[] { 1, 2, 4, 8, 16, 32, 64, 128 };

        #endregion

        //we support map drive that are mapped on a local fixed disk
        //we will resolve the fixed drive and automatically fix paths
        //so everything should be transparent
        string _locallyMappedDriveRootPath;
        string _rootPath;

        SafeFileHandle _volumeHandle;
        DiskInfoWrapper _diskInfo;
        readonly DriveInfo _driveInfo;
        readonly RetrieveMode _retrieveMode;
        byte[] _bitmapData;

        #region Properties

        //public IDiskInfo DiskInfo
        //{
        //    get { return _diskInfo; }
        //}

        public byte[] GetVolumeBitmap()
        {
            return _bitmapData;
        }

        #endregion

        #region Events

        /// <summary>
        /// Raised once the bitmap data has been read.
        /// </summary>
        public event EventHandler BitmapDataAvailable;

        private void OnBitmapDataAvailable()
        {
            if (BitmapDataAvailable != null)
                BitmapDataAvailable(this, EventArgs.Empty);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Try to resolve map drive if it points to a local volume.
        /// </summary>
        /// <param name="driveInfo"></param>
        /// <returns></returns>
        private DriveInfo ResolveLocalMapDrive(DriveInfo driveInfo)
        {
            StringBuilder remoteNameBuilder = new StringBuilder(2048);
            int len = remoteNameBuilder.MaxCapacity;

            //get the address on which the map drive is pointing
            WNetGetConnection(driveInfo.Name.TrimEnd(new char[] { '\\' }), remoteNameBuilder, ref len);

            string remoteName = remoteNameBuilder.ToString();
            if (string.IsNullOrEmpty(remoteName))
                throw new Exception("The drive is neither a local drive nor a locally mapped network drive, can't open volume.");

            //by getting all network shares on the local computer
            //we will be able to compare them with the remote address we found earlier.
            NetworkShare[] networkShares = EnumNetShares();

            for (int i = 0; i < networkShares.Length; ++i)
            {
                string networkShare =
                    string.Format(@"\\{0}\{1}", Environment.MachineName, networkShares[i].NetworkName);

                if (string.Equals(remoteName, networkShare, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(networkShares[i].LocalPath))
                {
                    _locallyMappedDriveRootPath = networkShares[i].LocalPath;
                    break;
                }
            }

            if (_locallyMappedDriveRootPath == null)
                throw new Exception("The drive is neither a local drive nor a locally mapped network drive, can't open volume.");

            return new DriveInfo(Path.GetPathRoot(_locallyMappedDriveRootPath));
        }


        //private string GetNameIndex(string name)
        //{
        //    return name;
        //}

        //private string GetNameFromIndex(string nameIndex)
        //{
        //    return nameIndex;
        //}

        private NtfsStream SearchStream(List<NtfsStream> streams, AttributeType streamType)
        {
            //since the number of stream is usually small, we can afford O(n)
            foreach (NtfsStream stream in streams)
                if (stream.Type == streamType)
                    return stream;

            return null;
        }

        private NtfsStream SearchStream(List<NtfsStream> streams, AttributeType streamType, string streamNameIndex)
        {
            //since the number of stream is usually small, we can afford O(n)
            foreach (NtfsStream stream in streams)
                if (stream.Type == streamType &&
                    stream.Name == streamNameIndex)
                    return stream;

            return null;
        }

        #endregion

        #region File Reading Wrappers

        private unsafe void ReadFile(byte* buffer, int len, UInt64 absolutePosition)
        {
            ReadFile(buffer, (UInt64)len, absolutePosition);
        }

        private unsafe void ReadFile(byte* buffer, UInt32 len, UInt64 absolutePosition)
        {
            ReadFile(buffer, (UInt64)len, absolutePosition);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void ReadFile(byte* buffer, UInt64 len, UInt64 absolutePosition)
        {
            NativeOverlapped overlapped = new NativeOverlapped(absolutePosition);

            uint read;
            if (!ReadFile(_volumeHandle, (IntPtr)buffer, (uint)len, out read, ref overlapped))
                throw new Exception("Unable to read volume information");

            if (read != (uint)len)
                throw new Exception("Unable to read volume information");
        }

        #endregion

        #region Ntfs Interpretor

        /// <summary>
        /// Read the next contiguous block of information on disk
        /// </summary>
        private unsafe bool ReadNextChunk(
            byte* buffer,
            UInt32 bufferSize,
            UInt32 nodeIndex,
            int fragmentIndex,
            NtfsStream dataNtfsStream,
            ref UInt64 BlockStart,
            ref UInt64 BlockEnd,
            ref UInt64 Vcn,
            ref UInt64 RealVcn
            )
        {
            BlockStart = nodeIndex;
            BlockEnd = BlockStart + bufferSize / _diskInfo.BytesPerMftRecord;
            if (BlockEnd > dataNtfsStream.Size * 8)
                BlockEnd = dataNtfsStream.Size * 8;

            UInt64 u1 = 0;

            int fragmentCount = dataNtfsStream.Fragments.Count;
            while (fragmentIndex < fragmentCount)
            {
                NtfsFragment ntfsFragment = dataNtfsStream.Fragments[fragmentIndex];

                /* Calculate Inode at the end of the fragment. */
                u1 = (RealVcn + ntfsFragment.NextVcn - Vcn) * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster / _diskInfo.BytesPerMftRecord;

                if (u1 > nodeIndex)
                    break;

                do
                {
                    if (ntfsFragment.Lcn != VIRTUALFRAGMENT)
                        RealVcn = RealVcn + ntfsFragment.NextVcn - Vcn;

                    Vcn = ntfsFragment.NextVcn;

                    if (++fragmentIndex >= fragmentCount)
                        break;

                } while (ntfsFragment.Lcn == VIRTUALFRAGMENT);
            }

            if (fragmentIndex >= fragmentCount)
                return false;

            if (BlockEnd >= u1)
                BlockEnd = u1;

            ulong position =
                (dataNtfsStream.Fragments[fragmentIndex].Lcn - RealVcn) * _diskInfo.BytesPerSector *
                    _diskInfo.SectorsPerCluster + BlockStart * _diskInfo.BytesPerMftRecord;

            ReadFile(buffer, (BlockEnd - BlockStart) * _diskInfo.BytesPerMftRecord, position);

            return true;
        }

        /// <summary>
        /// Gather basic disk information we need to interpret data
        /// </summary>
        private unsafe void InitializeDiskInfo()
        {
            byte[] volumeData = new byte[512];

            fixed (byte* ptr = volumeData)
            {
                ReadFile(ptr, volumeData.Length, 0);

                BootSector* bootSector = (BootSector*)ptr;

                if (bootSector->Signature != 0x202020205346544E)
                    throw new Exception("This is not an NTFS disk.");

                DiskInfoWrapper diskInfo = new DiskInfoWrapper();
                diskInfo.BytesPerSector = bootSector->BytesPerSector;
                diskInfo.SectorsPerCluster = bootSector->SectorsPerCluster;
                diskInfo.TotalSectors = bootSector->TotalSectors;
                diskInfo.MftStartLcn = bootSector->MftStartLcn;
                diskInfo.Mft2StartLcn = bootSector->Mft2StartLcn;
                diskInfo.ClustersPerMftRecord = bootSector->ClustersPerMftRecord;
                diskInfo.ClustersPerIndexRecord = bootSector->ClustersPerIndexRecord;

                if (bootSector->ClustersPerMftRecord >= 128)
                    diskInfo.BytesPerMftRecord = ((ulong)1 << (byte)(256 - (byte)bootSector->ClustersPerMftRecord));
                else
                    diskInfo.BytesPerMftRecord = diskInfo.ClustersPerMftRecord * diskInfo.BytesPerSector * diskInfo.SectorsPerCluster;

                diskInfo.BytesPerCluster = (UInt64)diskInfo.BytesPerSector * (UInt64)diskInfo.SectorsPerCluster;

                if (diskInfo.SectorsPerCluster > 0)
                    diskInfo.TotalClusters = diskInfo.TotalSectors / diskInfo.SectorsPerCluster;

                _diskInfo = diskInfo;
            }
        }

        /// <summary>
        /// Used to check/adjust data before we begin to interpret it
        /// </summary>
        private unsafe void FixupRawMftdata(byte* buffer, UInt64 len)
        {
            FileRecordHeader* ntfsFileRecordHeader = (FileRecordHeader*)buffer;

            if (ntfsFileRecordHeader->RecordHeader.Type != RecordType.File)
                return;

            UInt16* wordBuffer = (UInt16*)buffer;

            UInt16* UpdateSequenceArray = (UInt16*)(buffer + ntfsFileRecordHeader->RecordHeader.UsaOffset);
            UInt32 increment = (UInt32)_diskInfo.BytesPerSector / sizeof(UInt16);

            UInt32 Index = increment - 1;

            for (int i = 1; i < ntfsFileRecordHeader->RecordHeader.UsaCount; i++)
            {
                /* Check if we are inside the buffer. */
                if (Index * sizeof(UInt16) >= len)
                    throw new Exception("USA data indicates that data is missing, the MFT may be corrupt.");

                // Check if the last 2 bytes of the sector contain the Update Sequence Number.
                if (wordBuffer[Index] != UpdateSequenceArray[0])
                    throw new Exception("USA fixup word is not equal to the Update Sequence Number, the MFT may be corrupt.");

                /* Replace the last 2 bytes in the sector with the value from the Usa array. */
                wordBuffer[Index] = UpdateSequenceArray[i];
                Index = Index + increment;
            }
        }

        /// <summary>
        /// Decode the RunLength value.
        /// </summary>
        private static unsafe Int64 ProcessRunLength(byte* runData, UInt32 runDataLength, Int32 runLengthSize, ref UInt32 index)
        {
            Int64 runLength = 0;
            byte* runLengthBytes = (byte*)&runLength;
            for (int i = 0; i < runLengthSize; i++)
            {
                runLengthBytes[i] = runData[index];
                if (++index >= runDataLength)
                    throw new Exception("Datarun is longer than buffer, the MFT may be corrupt.");
            }
            return runLength;
        }

        /// <summary>
        /// Decode the RunOffset value.
        /// </summary>
        private static unsafe Int64 ProcessRunOffset(byte* runData, UInt32 runDataLength, Int32 runOffsetSize, ref UInt32 index)
        {
            Int64 runOffset = 0;
            byte* runOffsetBytes = (byte*)&runOffset;

            int i;
            for (i = 0; i < runOffsetSize; i++)
            {
                runOffsetBytes[i] = runData[index];
                if (++index >= runDataLength)
                    throw new Exception("Datarun is longer than buffer, the MFT may be corrupt.");
            }

            //process negative values
            if (runOffsetBytes[i - 1] >= 0x80)
                while (i < 8)
                    runOffsetBytes[i++] = 0xFF;

            return runOffset;
        }

        /// <summary>
        /// Read the data that is specified in a RunData list from disk into memory,
        /// skipping the first Offset bytes.
        /// </summary>
        private unsafe byte[] ProcessNonResidentData(
            byte* RunData,
            UInt32 RunDataLength,
            UInt64 Offset,         /* Bytes to skip from begin of data. */
            UInt64 WantedLength    /* Number of bytes to read. */
            )
        {
            /* Sanity check. */
            if (RunData == null || RunDataLength == 0)
                throw new Exception("nothing to read");

            if (WantedLength >= UInt32.MaxValue)
                throw new Exception("too many bytes to read");

            /* We have to round up the WantedLength to the nearest sector. For some
               reason or other Microsoft has decided that raw reading from disk can
               only be done by whole sector, even though ReadFile() accepts it's
               parameters in bytes. */
            if (WantedLength % _diskInfo.BytesPerSector > 0)
                WantedLength += _diskInfo.BytesPerSector - (WantedLength % _diskInfo.BytesPerSector);

            /* Walk through the RunData and read the requested data from disk. */
            UInt32 Index = 0;
            Int64 Lcn = 0;
            Int64 Vcn = 0;

            byte[] buffer = new byte[WantedLength];

            fixed (byte* bufPtr = buffer)
            {
                while (RunData[Index] != 0)
                {
                    /* Decode the RunData and calculate the next Lcn. */
                    Int32 RunLengthSize = (RunData[Index] & 0x0F);
                    Int32 RunOffsetSize = ((RunData[Index] & 0xF0) >> 4);

                    if (++Index >= RunDataLength)
                        throw new Exception("Error: datarun is longer than buffer, the MFT may be corrupt.");

                    Int64 RunLength =
                        ProcessRunLength(RunData, RunDataLength, RunLengthSize, ref Index);

                    Int64 RunOffset =
                        ProcessRunOffset(RunData, RunDataLength, RunOffsetSize, ref Index);

                    // Ignore virtual extents.
                    if (RunOffset == 0 || RunLength == 0)
                        continue;

                    Lcn += RunOffset;
                    Vcn += RunLength;

                    /* Determine how many and which bytes we want to read. If we don't need
                       any bytes from this extent then loop. */
                    UInt64 ExtentVcn = (UInt64)((Vcn - RunLength) * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);
                    UInt64 ExtentLcn = (UInt64)(Lcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);
                    UInt64 ExtentLength = (UInt64)(RunLength * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);

                    if (Offset >= ExtentVcn + ExtentLength)
                        continue;

                    if (Offset > ExtentVcn)
                    {
                        ExtentLcn = ExtentLcn + Offset - ExtentVcn;
                        ExtentLength = ExtentLength - (Offset - ExtentVcn);
                        ExtentVcn = Offset;
                    }

                    if (Offset + WantedLength <= ExtentVcn)
                        continue;

                    if (Offset + WantedLength < ExtentVcn + ExtentLength)
                        ExtentLength = Offset + WantedLength - ExtentVcn;

                    if (ExtentLength == 0)
                        continue;

                    ReadFile(bufPtr + ExtentVcn - Offset, ExtentLength, ExtentLcn);
                }
            }

            return buffer;
        }

        /// <summary>
        /// Process each attributes and gather information when necessary
        /// </summary>
        private unsafe void ProcessAttributes(ref Node node, UInt32 nodeIndex, byte* ptr, UInt64 BufLength, UInt16 instance, int depth, List<NtfsStream> streams, bool isMftNode)
        {
            Attribute* attribute = null;

            //AttributeList* attributeList = null;

//           Guid? fileReferenceNumber ;
            unchecked
            {
                for (uint AttributeOffset = 0; AttributeOffset < BufLength; AttributeOffset = AttributeOffset + attribute->Length)
            {
                attribute = (Attribute*)(ptr + AttributeOffset);

                // exit the loop if end-marker.
                if ((AttributeOffset + 4 <= BufLength) &&
                    (*(UInt32*)attribute == 0xFFFFFFFF))
                    break;

                //make sure we did read the data correctly
                if ((AttributeOffset + 4 > BufLength) || attribute->Length < 3 ||
                    (AttributeOffset + attribute->Length > BufLength))
                    throw new Exception("Error: attribute in Inode %I64u is bigger than the data, the MFT may be corrupt.");

                //attributes list needs to be processed at the end
                if (attribute->AttributeType == AttributeType.AttributeAttributeList)
                    continue;

                /* If the Instance does not equal the AttributeNumber then ignore the attribute.
                   This is used when an AttributeList is being processed and we only want a specific
                   instance. */
                if ((instance != 65535) && (instance != attribute->AttributeNumber))
                    continue;

                if (attribute->Nonresident == 0)
                {
                    ResidentAttribute* residentAttribute = (ResidentAttribute*)attribute;

                    // 数据不对，同磁盘下所有文件取到的值都一样
                    //// attributeList is no for required, residentAttribute->ValueOffset value is fixed
                    //var alPtr = ptr + 0 + residentAttribute->ValueOffset;
                    //attributeList = (AttributeList*)&alPtr[0];

                    /////* Exit if no more attributes. AttributeLists are usually not closed by the
                    ////   0xFFFFFFFF endmarker. Reaching the end of the buffer is therefore normal and
                    ////   not an error. */
                    ////if (AttributeOffset + 3 > bufLength) break;
                    ////if (*(UInt32*)attribute == 0xFFFFFFFF) break;
                    ////if (attribute->Length < 3) break;
                    ////if (AttributeOffset + attribute->Length > bufLength) break;

                    ///* Extract the referenced Inode. If it's the same as the calling Inode then ignore
                    //   (if we don't ignore then the program will loop forever, because for some
                    //   reason the info in the calling Inode is duplicated here...). */
                    //UInt64 RefInode = ((UInt64)attributeList->FileReferenceNumber.InodeNumberHighPart << 32) + attributeList->FileReferenceNumber.InodeNumberLowPart;

                    ////if (RefInode == node.NodeIndex)
                    ////    continue;


                    switch (attribute->AttributeType)
                    {
                        case AttributeType.AttributeFileName:
                            AttributeFileName* attributeFileName = (AttributeFileName*)(ptr + AttributeOffset + residentAttribute->ValueOffset);

                            if (attributeFileName->ParentDirectory.InodeNumberHighPart > 0)
                                throw new NotSupportedException("48 bits inode are not supported to reduce memory footprint.");

                            //node.ParentNodeIndex = ((UInt64)attributeFileName->ParentDirectory.InodeNumberHighPart << 32) + attributeFileName->ParentDirectory.InodeNumberLowPart;

                            //var val1 = ((UInt64)attributeFileName->ParentDirectory.InodeNumberHighPart << 32) + attributeFileName->ParentDirectory.InodeNumberLowPart;
                            //var val2 = attributeFileName->ParentDirectory.SequenceNumber;

                            // InodeNumberLowPart 似乎就是记录的 index

                            node.ParentNodeIndex = attributeFileName->ParentDirectory.InodeNumberLowPart;

//                                if (node.NameLength == 0 && attributeFileName->NameType == 1)
//                                {
//                                    node.NameLength = attributeFileName->NameLength;
//                                    //node.NamePtr = &attributeFileName->Name;
////                                    fixed (char* p = node.Name)
////                                    {
////                                        *p = attributeFileName->Name;
////                                    }
//                                    fixed (sbyte* p = node.Name)
//                                    {
//                                        var str = new string(&attributeFileName->Name, 0, attributeFileName->NameLength);
//                                        *p = &str;
//                                    }
//                                }
                            if ( attributeFileName->NameType == 1 && node.Name == null)
                            {
                                node.NameLength = attributeFileName->NameLength;
                                node.Name = new string(&attributeFileName->Name, 0, attributeFileName->NameLength);
                            }
                            //FileReferenceNumber = ((UInt64)attributeFileName->ParentDirectory.InodeNumberHighPart << 32) + attributeFileName->ParentDirectory.InodeNumberLowPart;

                            ////if (node.StandardInformation!=null)
                            //{
                            //    node.StandardInformation.FileReferenceNumber = FileReferenceNumber;
                            //}

                            break;

                        case AttributeType.AttributeStandardInformation:
                            AttributeStandardInformation* attributeStandardInformation = (AttributeStandardInformation*)(ptr + AttributeOffset + residentAttribute->ValueOffset);

                            node.Attributes |= (Attributes)attributeStandardInformation->FileAttributes;

                            //attributeStandardInformation->Usn
                            if ((_retrieveMode & RetrieveMode.StandardInformations) == RetrieveMode.StandardInformations)
                            {
                                //node.StandardInformation = Marshal.PtrToStructure<AttributeStandardInformation>(attributeStandardInformation);
                                node.StandardInformation =
                                  new StandardInformation(
                                      attributeStandardInformation->CreationTime,
                                      attributeStandardInformation->FileChangeTime,
                                      attributeStandardInformation->LastAccessTime
                                  );
//                                if (fileReferenceNumber!=null)
//                                {
//                                    node.StandardInformation.FileReferenceNumber = fileReferenceNumber.Value;
//                                }
                            }

                            break;

                        case AttributeType.AttributeData:
                            node.Size = residentAttribute->ValueLength;
                            break;
                        case AttributeType.AttributeObjectId:
//                            AttributeObjectId* fileId = (AttributeObjectId*)(ptr + AttributeOffset + residentAttribute->ValueOffset);
//                            //var oid = new string((char*)fileId->FileId);
//                            fileReferenceNumber = fileId->ObjectId;
//                            node.StandardInformation.FileReferenceNumber = fileReferenceNumber.Value;
                            //var oid = new string(&fileId->ObjectId, 0, 16);
                //            var oid = new string((char*)fileId->ObjectId);
                           // var oid = (new string((char*)(ptr + AttributeOffset + attribute->NameOffset), 0, (int)attribute->NameLength));
                            //Console.WriteLine(fileId->FileId);    

                            break;
                            //case AttributeType.AttributeAttributeList:
                            //   // if (fileReferenceNumber == 0)
                            //    {
                            //        fileReferenceNumber = ProcessAttributeList(
                            //               null,
                            //               node,
                            //               ptr + AttributeOffset + residentAttribute->ValueOffset,
                            //               residentAttribute->ValueLength,
                            //               depth
                            //               );
                            //        node.StandardInformation.FileReferenceNumber = fileReferenceNumber;
                            //    }
                            //    break;
                    }
                }
                else
                {
                    NonResidentAttribute* nonResidentAttribute = (NonResidentAttribute*)attribute;

                    //save the length (number of bytes) of the data.
                    if (attribute->AttributeType == AttributeType.AttributeData && node.Size == 0)
                        node.Size = nonResidentAttribute->DataSize;

                    if (streams != null)
                    {
                        //extract the stream name
                        string streamNameIndex = null;
                        if (attribute->NameLength > 0)
                            streamNameIndex = (new string((char*)(ptr + AttributeOffset + attribute->NameOffset), 0, (int)attribute->NameLength));

                        //find or create the stream
                        NtfsStream ntfsStream =
                            SearchStream(streams, attribute->AttributeType, streamNameIndex);

                        if (ntfsStream == null)
                        {
                            ntfsStream = new NtfsStream(streamNameIndex, attribute->AttributeType, nonResidentAttribute->DataSize);
                            streams.Add(ntfsStream);
                        }
                        else if (ntfsStream.Size == 0)
                            ntfsStream.Size = nonResidentAttribute->DataSize;

                        //we need the fragment of the MFTNode so retrieve them this time
                        //even if fragments aren't normally read
                        if (isMftNode || (_retrieveMode & RetrieveMode.Fragments) == RetrieveMode.Fragments)
                            ProcessFragments(
                                ref node,
                                ntfsStream,
                                ptr + AttributeOffset + nonResidentAttribute->RunArrayOffset,
                                attribute->Length - nonResidentAttribute->RunArrayOffset,
                                nonResidentAttribute->StartingVcn
                            );
                    }
                }
            }


            }


            //for (uint AttributeOffset = 0; AttributeOffset < BufLength; AttributeOffset = AttributeOffset + attribute->Length)
            //{
            //    attribute = (Attribute*)&ptr[AttributeOffset];

            //    if (*(UInt32*)attribute == 0xFFFFFFFF)
            //        break;

            //    if (attribute->AttributeType != AttributeType.AttributeAttributeList)
            //        continue;

            //    if (attribute->Nonresident == 0)
            //    {
            //        ResidentAttribute* residentAttribute = (ResidentAttribute*)attribute;

            //        fileReferenceNumber = ProcessAttributeList(
            //        _mftDataStream,
            //        node,
            //        ptr + AttributeOffset + residentAttribute->ValueOffset,
            //        residentAttribute->ValueLength,
            //        depth
            //        );
            //        node.StandardInformation.FileReferenceNumber = fileReferenceNumber;

            //    }
            //    //else
            //    //{
            //    //    NonResidentAttribute* nonResidentAttribute = (NonResidentAttribute*)attribute;

            //    //    byte[] buffer =
            //    //        ProcessNonResidentData(
            //    //            ptr + AttributeOffset + nonResidentAttribute->RunArrayOffset,
            //    //            attribute->Length - nonResidentAttribute->RunArrayOffset,
            //    //            0,
            //    //            nonResidentAttribute->DataSize
            //    //      );

            //    //    fixed (byte* bufPtr = buffer)
            //    //        ProcessAttributeList(node, bufPtr, nonResidentAttribute->DataSize, depth + 1);
            //    //}
            //}


            //for (uint AttributeOffset = 0; AttributeOffset < BufLength; AttributeOffset = AttributeOffset + attribute->Length)
            //{
            //    attribute = (Attribute*)&ptr[AttributeOffset];

            //    if (*(UInt32*)attribute == 0xFFFFFFFF)
            //        break;

            //    if (attribute->AttributeType != AttributeType.AttributeAttributeList)
            //        continue;

            //    if (attribute->Nonresident == 0)
            //    {
            //        ResidentAttribute* residentAttribute = (ResidentAttribute*)attribute;

            //        ProcessAttributeList(
            //            node,
            //            ptr + AttributeOffset + residentAttribute->ValueOffset,
            //            residentAttribute->ValueLength,
            //            depth
            //            );
            //    }
            //    else
            //    {
            //        NonResidentAttribute* nonResidentAttribute = (NonResidentAttribute*)attribute;

            //        byte[] buffer =
            //            ProcessNonResidentData(
            //                ptr + AttributeOffset + nonResidentAttribute->RunArrayOffset,
            //                attribute->Length - nonResidentAttribute->RunArrayOffset,
            //                0,
            //                nonResidentAttribute->DataSize
            //          );

            //        fixed (byte* bufPtr = buffer)
            //            ProcessAttributeList(node, bufPtr, nonResidentAttribute->DataSize, depth + 1);
            //    }
            //}

            if (streams != null && streams.Count > 0)
                node.Size = streams[0].Size;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="dataStream">MFT dataStream</param>
        /// <param name="node"></param>
        /// <param name="ptr"></param>
        /// <param name="bufLength"></param>
        /// <param name="depth"></param>
        /// <returns></returns>
        private unsafe UInt64 ProcessAttributeList(NtfsStream dataStream, Node node, byte* ptr, UInt64 bufLength, int depth)
        {
            if (ptr == null || bufLength == 0)
                return 0;

            if (depth > 1000)
                throw new Exception("Error: infinite attribute loop, the MFT may be corrupt.");

            AttributeList* attribute = null;
            for (uint AttributeOffset = 0; AttributeOffset < bufLength; AttributeOffset = AttributeOffset + attribute->Length)
            {
                attribute = (AttributeList*)&ptr[AttributeOffset];

                /* Exit if no more attributes. AttributeLists are usually not closed by the
                   0xFFFFFFFF endmarker. Reaching the end of the buffer is therefore normal and
                   not an error. */
                if (AttributeOffset + 3 > bufLength) break;
                if (*(UInt32*)attribute == 0xFFFFFFFF) break;
                if (attribute->Length < 3) break;
                if (AttributeOffset + attribute->Length > bufLength) break;

                /* Extract the referenced Inode. If it's the same as the calling Inode then ignore
                   (if we don't ignore then the program will loop forever, because for some
                   reason the info in the calling Inode is duplicated here...). */
                UInt64 RefInode = ((UInt64)attribute->FileReferenceNumber.InodeNumberHighPart << 32) + attribute->FileReferenceNumber.InodeNumberLowPart;
                //if (RefInode > 0)
                //{
                //    return RefInode;
                //}
                //if (RefInode == node.NodeIndex)
                //    continue;

                /* Extract the streamname. I don't know why AttributeLists can have names, and
                   the name is not used further down. It is only extracted for debugging purposes.
                   */
                string streamName;
                if (attribute->NameLength > 0)
                    streamName = new string((char*)((UInt64)ptr + AttributeOffset + attribute->NameOffset), 0, attribute->NameLength);

                /* Find the fragment in the MFT that contains the referenced Inode. */
                UInt64 Vcn = 0;
                UInt64 RealVcn = 0;
                UInt64 RefInodeVcn = (RefInode * _diskInfo.BytesPerMftRecord) / (UInt64)(_diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);

                //NtfsStream dataStream = null;
                //foreach (var stream in mftNode.Streams)
                //    if (stream.Type == AttributeType.AttributeData)
                //    {
                //        dataStream = stream;
                //        break;
                //    }

                NtfsFragment? fragment = null;
                for (int i = 0; i < dataStream.Fragments.Count; ++i)
                {
                    fragment = dataStream.Fragments[i];

                    if (fragment.Value.Lcn != VIRTUALFRAGMENT)
                    {
                        if ((RefInodeVcn >= RealVcn) && (RefInodeVcn < RealVcn + fragment.Value.NextVcn - Vcn))
                            break;

                        RealVcn = RealVcn + fragment.Value.NextVcn - Vcn;
                    }

                    Vcn = fragment.Value.NextVcn;
                }

                if (fragment == null)
                    throw new Exception("Error: Inode %I64u is an extension of Inode %I64u, but does not exist (outside the MFT).");

                /* Fetch the record of the referenced Inode from disk. */
                byte[] buffer = new byte[_diskInfo.BytesPerMftRecord];

                NativeOverlapped overlapped =
                    new NativeOverlapped(
                        fragment.Value.Lcn - RealVcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster + RefInode * _diskInfo.BytesPerMftRecord
                        );

                fixed (byte* bufPtr = buffer)
                {
                    uint read;
                    bool result =
                        ReadFile(
                            _volumeHandle,
                            (IntPtr)bufPtr,
                            (UInt32)_diskInfo.BytesPerMftRecord,
                            out read,
                            ref overlapped
                            );

                    if (!result)
                        return 0;
                        /// throw new Exception("error reading disk");

                        /* Fixup the raw data. */
                        FixupRawMftdata(bufPtr, _diskInfo.BytesPerMftRecord);

                    /* If the Inode is not in use then skip. */
                    FileRecordHeader* fileRecordHeader = (FileRecordHeader*)bufPtr;
                    if ((fileRecordHeader->Flags & 1) != 1)
                        continue;

                    ///* If the BaseInode inside the Inode is not the same as the calling Inode then
                    //   skip. */
                    UInt64 baseInode = ((UInt64)fileRecordHeader->BaseFileRecord.InodeNumberHighPart << 32) + fileRecordHeader->BaseFileRecord.InodeNumberLowPart;

                    //if (baseInode > 0)
                    {
                        return baseInode;
                    }

                    //if (node.NodeIndex != baseInode)
                    //    continue;

                    ///* Process the list of attributes in the Inode, by recursively calling the
                    //   ProcessAttributes() subroutine. */
                    //ProcessAttributes(
                    //    node,
                    //    bufPtr + fileRecordHeader->AttributeOffset,
                    //    _diskInfo.BytesPerMftRecord - fileRecordHeader->AttributeOffset,
                    //    attribute->Instance,
                    //    depth + 1
                    //    );
                }
            }
            return 0;
        }


        /// <summary>
        /// Process fragments for streams
        /// </summary>
        private unsafe void ProcessFragments(
            ref Node node,
            NtfsStream ntfsStream,
            byte* runData,
            UInt32 runDataLength,
            UInt64 StartingVcn)
        {
            if (runData == null)
                return;

            /* Walk through the RunData and add the extents. */
            uint index = 0;
            Int64 lcn = 0;
            Int64 vcn = (Int64)StartingVcn;
            int runOffsetSize = 0;
            int runLengthSize = 0;

            while (runData[index] != 0)
            {
                /* Decode the RunData and calculate the next Lcn. */
                runLengthSize = (runData[index] & 0x0F);
                runOffsetSize = ((runData[index] & 0xF0) >> 4);

                if (++index >= runDataLength)
                    throw new Exception("Error: datarun is longer than buffer, the MFT may be corrupt.");

                Int64 runLength =
                    ProcessRunLength(runData, runDataLength, runLengthSize, ref index);

                Int64 runOffset =
                    ProcessRunOffset(runData, runDataLength, runOffsetSize, ref index);

                lcn += runOffset;
                vcn += runLength;

                /* Add the size of the fragment to the total number of clusters.
                   There are two kinds of fragments: real and virtual. The latter do not
                   occupy clusters on disk, but are information used by compressed
                   and sparse files. */
                if (runOffset != 0)
                    ntfsStream.Clusters += (UInt64)runLength;

                ntfsStream.Fragments.Add(
                    new NtfsFragment(
                        runOffset == 0 ? VIRTUALFRAGMENT : (UInt64)lcn,
                        (UInt64)vcn
                    )
                );
            }
        }

        /// <summary>
        /// Process an actual MFT record from the buffer
        /// </summary>
        private unsafe bool ProcessMftRecord(byte* buffer, UInt64 length, UInt32 nodeIndex, out Node node, List<NtfsStream> streams, bool isMftNode)
        {
            node = new Node();

            node.NodeIndex = nodeIndex;

            FileRecordHeader* ntfsFileRecordHeader = (FileRecordHeader*)buffer;

            if (ntfsFileRecordHeader->RecordHeader.Type != RecordType.File)
                return false;

            //the inode is not in use
            if ((ntfsFileRecordHeader->Flags & 1) != 1)
                return false;

            UInt64 baseInode = ((UInt64)ntfsFileRecordHeader->BaseFileRecord.InodeNumberHighPart << 32) + ntfsFileRecordHeader->BaseFileRecord.InodeNumberLowPart;

            //This is an inode extension used in an AttributeAttributeList of another inode, don't parse it
            if (baseInode != 0)
                return false;

            if (ntfsFileRecordHeader->AttributeOffset >= length)
                throw new Exception("Error: attributes in Inode %I64u are outside the FILE record, the MFT may be corrupt.");

            if (ntfsFileRecordHeader->BytesInUse > length)
                throw new Exception("Error: in Inode %I64u the record is bigger than the size of the buffer, the MFT may be corrupt.");

            //make the file appear in the rootdirectory by default
            node.ParentNodeIndex = ROOTDIRECTORY;

            if ((ntfsFileRecordHeader->Flags & 2) == 2)
                node.Attributes |= Attributes.Directory;

            ProcessAttributes(ref node, nodeIndex, buffer + ntfsFileRecordHeader->AttributeOffset, length - ntfsFileRecordHeader->AttributeOffset, 65535, 0, streams, isMftNode);

            return true;
        }

        /// <summary>
        /// Process the bitmap data that contains information on inode usage.
        /// </summary>
        private unsafe byte[] ProcessBitmapData(List<NtfsStream> streams)
        {
            UInt64 Vcn = 0;
            UInt64 MaxMftBitmapBytes = 0;

            NtfsStream bitmapNtfsStream = SearchStream(streams, AttributeType.AttributeBitmap);
            if (bitmapNtfsStream == null)
                throw new Exception("No Bitmap Data");

            foreach (NtfsFragment fragment in bitmapNtfsStream.Fragments)
            {
                if (fragment.Lcn != VIRTUALFRAGMENT)
                    MaxMftBitmapBytes += (fragment.NextVcn - Vcn) * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster;

                Vcn = fragment.NextVcn;
            }

            byte[] bitmapData = new byte[MaxMftBitmapBytes];

            fixed (byte* bitmapDataPtr = bitmapData)
            {
                Vcn = 0;
                UInt64 RealVcn = 0;

                foreach (NtfsFragment fragment in bitmapNtfsStream.Fragments)
                {
                    if (fragment.Lcn != VIRTUALFRAGMENT)
                    {
                        ReadFile(
                            bitmapDataPtr + RealVcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster,
                            (fragment.NextVcn - Vcn) * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster,
                            fragment.Lcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster
                            );

                        RealVcn = RealVcn + fragment.NextVcn - Vcn;
                    }

                    Vcn = fragment.NextVcn;
                }
            }

            return bitmapData;
        }

        internal IEnumerable<Node> EnumFiles()
        {
            return ProcessMft();
        }

        NtfsStream _mftDataStream;
        /// <summary>
        /// Begin the process of interpreting MFT data
        /// </summary>
        private IEnumerable<Node> ProcessMft()
        {
            //64 KB seems to be optimal for Windows XP, Vista is happier with 256KB...
            uint bufferSize = (Environment.OSVersion.Version.Major >= 6 ? 256u : 64u) * 1024;

            //            if ((_retrieveMode & RetrieveMode.StandardInformations) == RetrieveMode.StandardInformations)
            //                _standardInformations = new StandardInformation[1]; //allocate some space for $MFT record

            List<NtfsStream> mftStreams = new List<NtfsStream>();

            byte[] data = new byte[bufferSize];

            ReadMftStreams(mftStreams, data);

            //the bitmap data contains all used inodes on the disk
            _bitmapData = ProcessBitmapData(mftStreams);

            OnBitmapDataAvailable();

            NtfsStream dataNtfsStream = SearchStream(mftStreams, AttributeType.AttributeData);

            this._mftDataStream = dataNtfsStream;

            UInt32 maxInode = (UInt32)_bitmapData.Length * 8;
            if (maxInode > (UInt32)(dataNtfsStream.Size / _diskInfo.BytesPerMftRecord))
                maxInode = (UInt32)(dataNtfsStream.Size / _diskInfo.BytesPerMftRecord);

            //Node[] nodes = new Node[maxInode];
            //nodes[0] = mftNode;

            //            if ((_retrieveMode & RetrieveMode.StandardInformations) == RetrieveMode.StandardInformations)
            //            {
            //                StandardInformation mftRecordInformation = _standardInformations[0];
            //                _standardInformations = new StandardInformation[maxInode];
            //                _standardInformations[0] = mftRecordInformation;
            //            }

            //            if ((_retrieveMode & RetrieveMode.Streams) == RetrieveMode.Streams)
            //                _streams = new Stream[maxInode][];


            /* Read and process all the records in the MFT. The records are read into a
                     buffer and then given one by one to the InterpretMftRecord() subroutine. */

            UInt64 BlockStart = 0, BlockEnd = 0;
            UInt64 RealVcn = 0, Vcn = 0;

            ulong totalBytesRead = 0;
            int fragmentIndex = 0;
            int fragmentCount = dataNtfsStream.Fragments.Count;

            (int, Node) ReadNextNode(UInt32 nodeIndex)
            {
                unsafe
                {
                    fixed (byte* buffer = data)
                    {
                        if (nodeIndex >= BlockEnd)
                        {
                            if (!ReadNextChunk(
                            buffer,
                            bufferSize,
                            nodeIndex,
                            fragmentIndex,
                            dataNtfsStream,
                            ref BlockStart,
                            ref BlockEnd,
                            ref Vcn,
                            ref RealVcn))
                                return (-1, default(Node));// break;

                            totalBytesRead += (BlockEnd - BlockStart) * _diskInfo.BytesPerMftRecord;
                        }

                        FixupRawMftdata(
                                buffer + (nodeIndex - BlockStart) * _diskInfo.BytesPerMftRecord,
                                _diskInfo.BytesPerMftRecord
                            );

                        List<NtfsStream> streams = null;
                        if ((_retrieveMode & RetrieveMode.Streams) == RetrieveMode.Streams)
                            streams = new List<NtfsStream>();

                        Node newNode;
                        if (!ProcessMftRecord(
                                buffer + (nodeIndex - BlockStart) * _diskInfo.BytesPerMftRecord,
                                _diskInfo.BytesPerMftRecord,
                                nodeIndex,
                                out newNode,
                                streams,
                                false))
                        {
                            return (0, default(Node));//continue;
                        }
                        else
                        {
                            return (1, newNode);//continue;
                        }
                    }
                }
            }


            //Stopwatch stopwatch = new Stopwatch();
            //stopwatch.Start();


            for (UInt32 nodeIndex = 1; nodeIndex < maxInode; nodeIndex++)
            {
                // Ignore the Inode if the bitmap says it's not in use.
                if ((_bitmapData[nodeIndex >> 3] & BitmapMasks[nodeIndex % 8]) == 0)
                    continue;
                var ret = ReadNextNode(nodeIndex);
                switch (ret.Item1)
                {
                    case 0:
                        continue;
                    case -1:
                        break;
                    case 1:
                    default:
                        //nodes[nodeIndex] = newNode;
                        //if (streams != null)
                        //    _streams[nodeIndex] = streams.ToArray();
                        //ret.Item2.Streams = streams;
                        //if (ret.Item2.Name == null)
                        if (ret.Item2.NameLength == 0)
                        {
                            continue;
                        }
                        else
                        {
                            yield return ret.Item2;
                        }
                        break;
                }
            }

            //stopwatch.Stop();

            //Trace.WriteLine(
            //    string.Format(
            //        "{0:F3} MB of volume metadata has been read in {1:F3} s at {2:F3} MB/s", 
            //        (float)totalBytesRead / (1024*1024),
            //        (float)stopwatch.Elapsed.TotalSeconds,
            //        ((float)totalBytesRead / (1024*1024)) / stopwatch.Elapsed.TotalSeconds
            //    )
            //);

            //return nodes;
        }

        private void ReadMftStreams(List<NtfsStream> mftStreams, byte[] data)
        {
            unsafe
            {
                fixed (byte* buffer = data)
                {
                    //Read the $MFT record from disk into memory, which is always the first record in the MFT. 
                    ReadFile(buffer, _diskInfo.BytesPerMftRecord, _diskInfo.MftStartLcn * _diskInfo.BytesPerSector * _diskInfo.SectorsPerCluster);

                    //Fixup the raw data from disk. This will also test if it's a valid $MFT record.
                    FixupRawMftdata(buffer, _diskInfo.BytesPerMftRecord);
                    /*
                    the root Node:
                    nodes[0] = mftNode;
                     */
                    Node mftNode;
                    if (!ProcessMftRecord(buffer, _diskInfo.BytesPerMftRecord, 0, out mftNode, mftStreams, true))
                        throw new Exception("Can't interpret Mft Record");
                }
            }
        }

        #endregion
    }
}
