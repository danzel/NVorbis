﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;

namespace NVorbis.Ogg
{
    internal class ThreadSafeStream : Stream
    {
        class Node
        {
            public Thread Thread;
            public long Position;
            public int HitCount;
        }

        Stream _baseStream;
        LinkedList<Node> _positions;
        long _length;

        object _streamLock;
        ReaderWriterLockSlim _nodeLock;

        internal ThreadSafeStream(Stream baseStream)
        {
            if (!baseStream.CanSeek)
            {
                throw new ArgumentException("The stream must be seekable.", "baseStream");
            }

            _baseStream = baseStream;
            _positions = new LinkedList<Node>();

            _streamLock = new object();
            _nodeLock = new ReaderWriterLockSlim();

            _length = baseStream.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _positions.Clear();

                if (_nodeLock != null)
                {
                    _nodeLock.Dispose();
                    _nodeLock = null;
                }
            }

            base.Dispose(disposing);
        }

        public override bool CanRead
        {
            get { return _baseStream.CanRead; }
        }

        public override bool CanSeek
        {
            get { return _baseStream.CanSeek; }
        }

        public override bool CanWrite
        {
            get { return _baseStream.CanWrite; }
        }

        public override void Flush()
        {
            lock (_streamLock)
            {
                _baseStream.Flush();
            }
        }

        public override long Length
        {
            get { return _length; }
        }

        int _threadChangeCounter;
        Node _lastNode;

        Node GetNode()
        {
            // try to short-circuit the expensive operations below
            var nodeVal = _lastNode;
            if (nodeVal != null && nodeVal.Thread == Thread.CurrentThread)
            {
                return nodeVal;
            }

            if (Interlocked.Increment(ref _threadChangeCounter) % 50 == 0)
            {
                // try to sort...
                var upgraded = false;
                _nodeLock.EnterUpgradeableReadLock();
                try
                {
                    var curNode = _positions.First;
                    while (curNode != null)
                    {
                        var chkNode = curNode;
                        while (chkNode.Previous != null && chkNode.Previous.Value.HitCount < curNode.Value.HitCount)
                        {
                            chkNode = chkNode.Previous;
                        }
                        if (chkNode != curNode)
                        {
                            var temp = curNode.Next;
                            if (!upgraded)
                            {
                                _nodeLock.EnterWriteLock();
                                upgraded = true;
                            }
                            _positions.Remove(curNode);
                            _positions.AddBefore(chkNode, curNode);
                            curNode = temp;
                        }
                        else
                        {
                            curNode = curNode.Next;
                        }
                    }
                }
                finally
                {
                    if (upgraded)
                    {
                        _nodeLock.ExitWriteLock();
                    }

                    _nodeLock.ExitUpgradeableReadLock();
                }
            }

            LinkedListNode<Node> node;

            var thread = Thread.CurrentThread;
            _nodeLock.EnterReadLock();
            try
            {
                node = _positions.First;
                while (node != null)
                {
                    if (node.Value.Thread == thread)
                    {
                        // found it!
                        ++node.Value.HitCount;
                        return (_lastNode = node.Value);
                    }
                    node = node.Next;
                }
            }
            finally
            {
                _nodeLock.ExitReadLock();
            }

            // not found, create a new node and add it
            node = new LinkedListNode<Node>(
                new Node
                {
                    Thread = thread,
                    Position = 0L,
                    HitCount = 1,
                }
            );

            _nodeLock.EnterWriteLock();
            try
            {
                _positions.AddLast(node);
            }
            finally
            {
                _nodeLock.ExitWriteLock();
            }

            return node.Value;
        }

        public override long Position
        {
            get
            {
                return GetNode().Position;
            }
            set
            {
                Seek(value, SeekOrigin.Begin);
            }
        }

        byte[] _singleByteReadBuffer = new byte[1];
        public override int ReadByte()
        {
            // ugh... this gets called *a lot*, so reimplement the stream lock and use a shared buffer
            var node = GetNode();
            int val = -1;

            lock (_streamLock)
            {
                if (_baseStream.Position != node.Position) _baseStream.Position = node.Position;
                if (_baseStream.Read(_singleByteReadBuffer, 0, 1) == 1)
                {
                    val = _singleByteReadBuffer[0];
                }
            }

            if (val > -1)
            {
                node.Position++;
            }

            return val;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var node = GetNode();
            int cnt;

            lock (_streamLock)
            {
                if (_baseStream.Position != node.Position) _baseStream.Position = node.Position;
                cnt = _baseStream.Read(buffer, offset, count);
            }

            node.Position += cnt;

            return cnt;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!CanSeek) throw new InvalidOperationException();

            var node = GetNode();
            long pos = 0L;

            switch (origin)
            {
                case SeekOrigin.Begin:
                    pos = offset;
                    break;
                case SeekOrigin.End:
                    pos = Length + offset;
                    break;
                case SeekOrigin.Current:
                    pos = node.Position + offset;
                    break;
            }

            if (pos < 0L || pos > Length) throw new ArgumentOutOfRangeException("offset");

            return (node.Position = pos);
        }

        public override void SetLength(long value)
        {
            lock (_streamLock)
            {
                _baseStream.SetLength(value);
                _length = value;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var node = GetNode();

            lock (_streamLock)
            {
                if (_baseStream.Position != node.Position) _baseStream.Position = node.Position;
                _baseStream.Write(buffer, offset, count);
                _length = _baseStream.Length;
            }

            node.Position += count;
        }
    }
}