namespace System.IO.Filesystem.Ntfs
{
        /// <summary>
        /// Simple structure of available disk informations.
        /// </summary>
          sealed class DiskInfoWrapper : IDiskInfo
        {
            public UInt16 BytesPerSector;
            public byte SectorsPerCluster;
            public UInt64 TotalSectors;
            public UInt64 MftStartLcn;
            public UInt64 Mft2StartLcn;
            public UInt32 ClustersPerMftRecord;
            public UInt32 ClustersPerIndexRecord;
            public UInt64 BytesPerMftRecord;
            public UInt64 BytesPerCluster;
            public UInt64 TotalClusters;

            #region IDiskInfo Members

            ushort IDiskInfo.BytesPerSector
            {
                get { return BytesPerSector; }
            }

            byte IDiskInfo.SectorsPerCluster
            {
                get { return SectorsPerCluster; }
            }

            ulong IDiskInfo.TotalSectors
            {
                get { return TotalSectors; }
            }

            ulong IDiskInfo.MftStartLcn
            {
                get { return MftStartLcn; }
            }

            ulong IDiskInfo.Mft2StartLcn
            {
                get { return Mft2StartLcn; }
            }

            uint IDiskInfo.ClustersPerMftRecord
            {
                get { return ClustersPerMftRecord; }
            }

            uint IDiskInfo.ClustersPerIndexRecord
            {
                get { return ClustersPerIndexRecord; }
            }

            ulong IDiskInfo.BytesPerMftRecord
            {
                get { return BytesPerMftRecord; }
            }

            ulong IDiskInfo.BytesPerCluster
            {
                get { return BytesPerCluster; }
            }

            ulong IDiskInfo.TotalClusters
            {
                get { return TotalClusters; }
            }

            #endregion
        }
}