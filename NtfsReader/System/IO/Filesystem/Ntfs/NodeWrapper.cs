using System.Collections.Generic;

namespace System.IO.Filesystem.Ntfs
{
    /// <summary>
    /// Add some functionality to the basic node
    /// </summary>
    sealed class NodeWrapper //: INode
    {
        Node _node;
        string _fullName;

        public NodeWrapper(Node node)
        {
            _node = node;
        }

        public UInt32 NodeIndex
        {
            get { return _node.NodeIndex; }
        }

        public UInt32 ParentNodeIndex
        {
            get { return _node.ParentNodeIndex; }
        }

        public Attributes Attributes
        {
            get { return _node.Attributes; }
        }

        public string Name
        {
            get { return _node.Name; }
        }

        public UInt64 Size
        {
            get { return _node.Size; }
        }

        //        public string FullName
        //        {
        //            get
        //            {
        //                if (_fullName == null)
        //                    _fullName = _reader.GetNodeFullNameCore(_nodeIndex);
        //
        //                return _fullName;
        //            }
        //        }



        #region INode Members

        DateTime? creationTime;
        public DateTime CreationTime
        {
            get
            {
                if (creationTime == null) creationTime = DateTime.FromFileTimeUtc((Int64)this._node.StandardInformation.CreationTime);
                return creationTime.Value;
            }
        }


        DateTime? lastChangeTime;
        public DateTime LastChangeTime
        {
            get
            {
                if (lastChangeTime == null) lastChangeTime = DateTime.FromFileTimeUtc((Int64)this._node.StandardInformation.LastChangeTime);
                return lastChangeTime.Value;
            }
        }

        DateTime? lastAccessTime;
        public DateTime LastAccessTime
        {
            get
            {
                if (lastAccessTime == null) lastAccessTime = DateTime.FromFileTimeUtc((Int64)this._node.StandardInformation.LastAccessTime);
                return lastAccessTime.Value;
            }
        }

        #endregion
    }
}

