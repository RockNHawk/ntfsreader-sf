using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.IO.Filesystem.Ntfs
{
    #region classes

    internal sealed class NtfsStream
    {
        public UInt64 Clusters; // Total number of clusters.
        public UInt64 Size; // Total number of bytes.
        public AttributeType Type;
        public readonly string Name;
        public List<NtfsFragment> _fragments;

        public NtfsStream(string name, AttributeType type, UInt64 size)
        {
            Name = name;
            Type = type;
            Size = size;
        }

        public List<NtfsFragment> Fragments
        {
            get
            {
                if (_fragments == null)
                    _fragments = new List<NtfsFragment>(5);

                return _fragments;
            }
        }

    }

    /// <summary>
    /// Node struct for file and directory entries
    /// </summary>
    /// <remarks>
    /// We keep this as small as possible to reduce footprint for large volume.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 445)]
    [Spreads.Serialization.BinarySerialization(445)]
    internal unsafe struct Node
    {
        public Attributes Attributes;
        public UInt32 NodeIndex;
        public UInt32 ParentNodeIndex;
        public UInt64 Size;
        public StandardInformation StandardInformation;
        public string Name;

        /*
         * Name = new string(&attributeFileName->Name, 0, attributeFileName->NameLength);
         */
        public byte NameLength;
        //public char* NamePtr;

        //public fixed sbyte Name[200];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string GetName()
        {
            return this.Name;
            //var s = new string(NamePtr, 0, NameLength);
            //return s;
//            fixed (sbyte* p = Name)
//            {
//                return new string(p, 0, NameLength);
//            }
        }
        //public Span<char> GetNameSpan()
        //{
        //    //fixed (char* p = Name)
        //    //{
        //    //    return new Span<char>(p, NameLength);
        //    //}
        //    return new Span<char>(NamePtr, NameLength);
        //}
    }

    /// <summary>
    /// Contains extra information not required for basic purposes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    // [Spreads.Serialization.BinarySerialization]
    struct StandardInformation
    {
        public UInt64 CreationTime;
        public UInt64 LastAccessTime;
        public UInt64 LastChangeTime;
        // public Guid FileReferenceNumber;

        public StandardInformation(
            UInt64 creationTime,
            UInt64 lastAccessTime,
            UInt64 lastChangeTime
        // Guid fileReferenceNumber
        )
        {
            CreationTime = creationTime;
            LastAccessTime = lastAccessTime;
            LastChangeTime = lastChangeTime;
            // FileReferenceNumber = fileReferenceNumber;
        }
    }
    /*
        /// <summary>
        /// Add some functionality to the basic stream
        /// </summary>
        sealed class NtfsFragmentWrapper : IFragment
        {
            NtfsStreamWrapper _owner;
            NtfsFragment _ntfsFragment;

            public NtfsFragmentWrapper(NtfsStreamWrapper owner, NtfsFragment ntfsFragment)
            {
                _owner = owner;
                _ntfsFragment = ntfsFragment;
            }

            #region IFragment Members

            public ulong Lcn
            {
                get { return _ntfsFragment.Lcn; }
            }

            public ulong NextVcn
            {
                get { return _ntfsFragment.NextVcn; }
            }

            #endregion
        }
    */

    /*
    /// <summary>
    /// Add some functionality to the basic stream
    /// </summary>
    sealed class NtfsStreamWrapper : IStream
    {
        NtfsReader _reader;
        NodeWrapper _parentNode;
        int _streamIndex;

        public NtfsStreamWrapper(NtfsReader reader, NodeWrapper parentNode, int streamIndex)
        {
            _reader = reader;
            _parentNode = parentNode;
            _streamIndex = streamIndex;
        }

        #region IStream Members

        public string Name
        {
            get { return _parentNode.Streams[_streamIndex].Name; }
        }

        public UInt64 Size
        {
            get { return _parentNode.Streams[_streamIndex].Size; }
        }

        public IList<NtfsFragment> Fragments
        {
            get
            {
                //if ((_reader._retrieveMode & RetrieveMode.Fragments) != RetrieveMode.Fragments)
                //    throw new NotSupportedException("The fragments haven't been retrieved. Make sure to use the proper RetrieveMode.");

                return _parentNode.Streams[_streamIndex].Fragments;
                
                //var fragments = _parentNode.Streams[_streamIndex].Fragments;

//                if (fragments == null || fragments.Count == 0)
//                    return null;
//
//                List<NtfsFragment> newFragments = new List<NtfsFragment>();
//                foreach (var fragment in fragments)
//                    newFragments.Add(new NtfsFragmentWrapper(this, fragment));
//
//                return newFragments;
            }
        }

        #endregion
    }
*/

    #endregion
}