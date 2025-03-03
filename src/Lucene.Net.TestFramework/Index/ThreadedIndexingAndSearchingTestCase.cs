using J2N.Threading;
using J2N.Threading.Atomic;
using Lucene.Net.Analysis;
using Lucene.Net.Diagnostics;
using Lucene.Net.Documents;
using Lucene.Net.Index.Extensions;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Support;
using Lucene.Net.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Console = Lucene.Net.Util.SystemConsole;
using Directory = Lucene.Net.Store.Directory;

namespace Lucene.Net.Index
{
    /*
     * Licensed to the Apache Software Foundation (ASF) under one or more
     * contributor license agreements.  See the NOTICE file distributed with
     * this work for additional information regarding copyright ownership.
     * The ASF licenses this file to You under the Apache License, Version 2.0
     * (the "License"); you may not use this file except in compliance with
     * the License.  You may obtain a copy of the License at
     *
     *     http://www.apache.org/licenses/LICENSE-2.0
     *
     * Unless required by applicable law or agreed to in writing, software
     * distributed under the License is distributed on an "AS IS" BASIS,
     * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     * See the License for the specific language governing permissions and
     * limitations under the License.
     */

    // TODO
    //   - mix in forceMerge, addIndexes
    //   - randomly mix in non-congruent docs

    /// <summary>
    /// Utility class that spawns multiple indexing and
    /// searching threads.
    /// </summary>
    public abstract class ThreadedIndexingAndSearchingTestCase : LuceneTestCase
#if TESTFRAMEWORK_XUNIT
        , Xunit.IClassFixture<BeforeAfterClass>
    {
        public ThreadedIndexingAndSearchingTestCase(BeforeAfterClass beforeAfter)
            : base(beforeAfter)
        {
        }
#else
    {
#endif
        protected readonly AtomicBoolean m_failed = new AtomicBoolean();
        protected readonly AtomicInt32 m_addCount = new AtomicInt32();
        protected readonly AtomicInt32 m_delCount = new AtomicInt32();
        protected readonly AtomicInt32 m_packCount = new AtomicInt32();

        protected Directory m_dir;
        protected IndexWriter m_writer;

        private class SubDocs
        {
            public string PackID { get; private set; }
            public IList<string> SubIDs { get; private set; }
            public bool Deleted { get; set; }

            public SubDocs(string packID, IList<string> subIDs)
            {
                this.PackID = packID;
                this.SubIDs = subIDs;
            }
        }

        // LUCENENET specific - Specify to unzip the line file docs
        public override void BeforeClass()
        {
            UseTempLineDocsFile = true;
            base.BeforeClass();
        }

        // Called per-search
        protected abstract IndexSearcher GetCurrentSearcher();

        protected abstract IndexSearcher GetFinalSearcher();

        protected virtual void ReleaseSearcher(IndexSearcher s)
        {
        }

        // Called once to run searching
        protected abstract void DoSearching(TaskScheduler es, long stopTime);

        protected virtual Directory GetDirectory(Directory @in)
        {
            return @in;
        }

        protected virtual void UpdateDocuments(Term id, IEnumerable<IEnumerable<IIndexableField>> docs)
        {
            m_writer.UpdateDocuments(id, docs);
        }

        protected virtual void AddDocuments(Term id, IEnumerable<IEnumerable<IIndexableField>> docs)
        {
            m_writer.AddDocuments(docs);
        }

        protected virtual void AddDocument(Term id, IEnumerable<IIndexableField> doc)
        {
            m_writer.AddDocument(doc);
        }

        protected virtual void UpdateDocument(Term term, IEnumerable<IIndexableField> doc)
        {
            m_writer.UpdateDocument(term, doc);
        }

        protected virtual void DeleteDocuments(Term term)
        {
            m_writer.DeleteDocuments(term);
        }

        protected virtual void DoAfterIndexingThreadDone()
        {
        }

        private ThreadJob[] LaunchIndexingThreads(LineFileDocs docs, 
                                                    int numThreads, 
                                                    long stopTime, 
                                                    ISet<string> delIDs, 
                                                    ISet<string> delPackIDs,
                                                    ConcurrentQueue<SubDocs> allSubDocs)
        {
            ThreadJob[] threads = new ThreadJob[numThreads];
            for (int thread = 0; thread < numThreads; thread++)
            {
                threads[thread] = new ThreadAnonymousClass(this, docs, stopTime, delIDs, delPackIDs, allSubDocs);
                threads[thread].IsBackground = (true);
                threads[thread].Start();
            }

            return threads;
        }

        private class ThreadAnonymousClass : ThreadJob
        {
            private readonly ThreadedIndexingAndSearchingTestCase outerInstance;

            private readonly LineFileDocs docs;
            private readonly long stopTime;
            private readonly ISet<string> delIDs;
            private readonly ISet<string> delPackIDs;
            private readonly ConcurrentQueue<SubDocs> allSubDocs;

            public ThreadAnonymousClass(ThreadedIndexingAndSearchingTestCase outerInstance, LineFileDocs docs, long stopTime, ISet<string> delIDs, ISet<string> delPackIDs, ConcurrentQueue<SubDocs> allSubDocs)
            {
                this.outerInstance = outerInstance;
                this.docs = docs;
                this.stopTime = stopTime;
                this.delIDs = delIDs;
                this.delPackIDs = delPackIDs;
                this.allSubDocs = allSubDocs;
            }

            public override void Run()
            {
                // TODO: would be better if this were cross thread, so that we make sure one thread deleting anothers added docs works:
                IList<string> toDeleteIDs = new List<string>();
                IList<SubDocs> toDeleteSubDocs = new List<SubDocs>();
                while (Environment.TickCount < stopTime && !outerInstance.m_failed)
                {
                    try
                    {
                        // Occasional longish pause if running
                        // nightly
                        if (LuceneTestCase.TestNightly && Random.Next(6) == 3)
                        {
                            if (Verbose)
                            {
                                Console.WriteLine(Thread.CurrentThread.Name + ": now long sleep");
                            }
                            //Thread.Sleep(TestUtil.NextInt32(Random, 50, 500));
                            // LUCENENET specific - Reduced amount of pause to keep the total
                            // Nightly test time under 1 hour
                            Thread.Sleep(TestUtil.NextInt32(Random, 50, 250));
                        }

                        // Rate limit ingest rate:
                        if (Random.Next(7) == 5)
                        {
                            Thread.Sleep(TestUtil.NextInt32(Random, 1, 10));
                            if (Verbose)
                            {
                                Console.WriteLine(Thread.CurrentThread.Name + ": done sleep");
                            }
                        }

                        Document doc = docs.NextDoc();
                        if (doc == null)
                        {
                            break;
                        }

                        // Maybe add randomly named field
                        string addedField;
                        if (Random.NextBoolean())
                        {
                            addedField = "extra" + Random.Next(40);
                            doc.Add(NewTextField(addedField, "a random field", Field.Store.YES));
                        }
                        else
                        {
                            addedField = null;
                        }

                        if (Random.NextBoolean())
                        {
                            if (Random.NextBoolean())
                            {
                                // Add/update doc block:
                                string packID;
                                SubDocs delSubDocs;
                                if (toDeleteSubDocs.Count > 0 && Random.NextBoolean())
                                {
                                    delSubDocs = toDeleteSubDocs[Random.Next(toDeleteSubDocs.Count)];
                                    if (Debugging.AssertsEnabled) Debugging.Assert(!delSubDocs.Deleted);
                                    toDeleteSubDocs.Remove(delSubDocs);
                                    // Update doc block, replacing prior packID
                                    packID = delSubDocs.PackID;
                                }
                                else
                                {
                                    delSubDocs = null;
                                    // Add doc block, using new packID
                                    packID = outerInstance.m_packCount.GetAndIncrement().ToString(CultureInfo.InvariantCulture);
                                }

                                Field packIDField = NewStringField("packID", packID, Field.Store.YES);
                                IList<string> docIDs = new List<string>();
                                SubDocs subDocs = new SubDocs(packID, docIDs);
                                IList<Document> docsList = new List<Document>();

                                allSubDocs.Enqueue(subDocs);
                                doc.Add(packIDField);
                                docsList.Add(TestUtil.CloneDocument(doc));
                                docIDs.Add(doc.Get("docid"));

                                int maxDocCount = TestUtil.NextInt32(Random, 1, 10);
                                while (docsList.Count < maxDocCount)
                                {
                                    doc = docs.NextDoc();
                                    if (doc == null)
                                    {
                                        break;
                                    }
                                    docsList.Add(TestUtil.CloneDocument(doc));
                                    docIDs.Add(doc.Get("docid"));
                                }
                                outerInstance.m_addCount.AddAndGet(docsList.Count);

                                Term packIDTerm = new Term("packID", packID);

                                if (delSubDocs != null)
                                {
                                    delSubDocs.Deleted = true;
                                    delIDs.UnionWith(delSubDocs.SubIDs);
                                    outerInstance.m_delCount.AddAndGet(delSubDocs.SubIDs.Count);
                                    if (Verbose)
                                    {
                                        Console.WriteLine(Thread.CurrentThread.Name + ": update pack packID=" + delSubDocs.PackID + 
                                            " count=" + docsList.Count + " docs=" + string.Format(J2N.Text.StringFormatter.InvariantCulture, "{0}", docIDs));
                                    }
                                    outerInstance.UpdateDocuments(packIDTerm, docsList);
                                }
                                else
                                {
                                    if (Verbose)
                                    {
                                        Console.WriteLine(Thread.CurrentThread.Name + ": add pack packID=" + packID + 
                                            " count=" + docsList.Count + " docs=" + string.Format(J2N.Text.StringFormatter.InvariantCulture, "{0}", docIDs));
                                    }
                                    outerInstance.AddDocuments(packIDTerm, docsList);
                                }
                                doc.RemoveField("packID");

                                if (Random.Next(5) == 2)
                                {
                                    if (Verbose)
                                    {
                                        Console.WriteLine(Thread.CurrentThread.Name + ": buffer del id:" + packID);
                                    }
                                    toDeleteSubDocs.Add(subDocs);
                                }
                            }
                            else
                            {
                                // Add single doc
                                string docid = doc.Get("docid");
                                if (Verbose)
                                {
                                    Console.WriteLine(Thread.CurrentThread.Name + ": add doc docid:" + docid);
                                }
                                outerInstance.AddDocument(new Term("docid", docid), doc);
                                outerInstance.m_addCount.GetAndIncrement();

                                if (Random.Next(5) == 3)
                                {
                                    if (Verbose)
                                    {
                                        Console.WriteLine(Thread.CurrentThread.Name + ": buffer del id:" + doc.Get("docid"));
                                    }
                                    toDeleteIDs.Add(docid);
                                }
                            }
                        }
                        else
                        {
                            // Update single doc, but we never re-use
                            // and ID so the delete will never
                            // actually happen:
                            if (Verbose)
                            {
                                Console.WriteLine(Thread.CurrentThread.Name + ": update doc id:" + doc.Get("docid"));
                            }
                            string docid = doc.Get("docid");
                            outerInstance.UpdateDocument(new Term("docid", docid), doc);
                            outerInstance.m_addCount.GetAndIncrement();

                            if (Random.Next(5) == 3)
                            {
                                if (Verbose)
                                {
                                    Console.WriteLine(Thread.CurrentThread.Name + ": buffer del id:" + doc.Get("docid"));
                                }
                                toDeleteIDs.Add(docid);
                            }
                        }

                        if (Random.Next(30) == 17)
                        {
                            if (Verbose)
                            {
                                Console.WriteLine(Thread.CurrentThread.Name + ": apply " + toDeleteIDs.Count + " deletes");
                            }
                            foreach (string id in toDeleteIDs)
                            {
                                if (Verbose)
                                {
                                    Console.WriteLine(Thread.CurrentThread.Name + ": del term=id:" + id);
                                }
                                outerInstance.DeleteDocuments(new Term("docid", id));
                            }
                            int count = outerInstance.m_delCount.AddAndGet(toDeleteIDs.Count);
                            if (Verbose)
                            {
                                Console.WriteLine(Thread.CurrentThread.Name + ": tot " + count + " deletes");
                            }
                            delIDs.UnionWith(toDeleteIDs);
                            toDeleteIDs.Clear();

                            foreach (SubDocs subDocs in toDeleteSubDocs)
                            {
                                if (Debugging.AssertsEnabled) Debugging.Assert(!subDocs.Deleted);
                                delPackIDs.Add(subDocs.PackID);
                                outerInstance.DeleteDocuments(new Term("packID", subDocs.PackID));
                                subDocs.Deleted = true;
                                if (Verbose)
                                {
                                    Console.WriteLine(Thread.CurrentThread.Name + ": del subs: " + subDocs.SubIDs + " packID=" + subDocs.PackID);
                                }
                                delIDs.UnionWith(subDocs.SubIDs);
                                outerInstance.m_delCount.AddAndGet(subDocs.SubIDs.Count);
                            }
                            toDeleteSubDocs.Clear();
                        }
                        if (addedField != null)
                        {
                            doc.RemoveField(addedField);
                        }
                    }
                    catch (Exception t)
                    {
                        Console.WriteLine(Thread.CurrentThread.Name + ": hit exc");
                        Console.WriteLine(t.ToString());
                        Console.Write(t.StackTrace);
                        outerInstance.m_failed.Value = (true);
                        throw new Exception(t.ToString(), t);
                    }
                }
                if (Verbose)
                {
                    Console.WriteLine(Thread.CurrentThread.Name + ": indexing done");
                }

                outerInstance.DoAfterIndexingThreadDone();
            }
        }

        protected virtual void RunSearchThreads(long stopTime)
        {
            int numThreads = TestUtil.NextInt32(Random, 1, 5);
            ThreadJob[] searchThreads = new ThreadJob[numThreads];
            AtomicInt32 totHits = new AtomicInt32();

            // silly starting guess:
            AtomicInt32 totTermCount = new AtomicInt32(100);

            // TODO: we should enrich this to do more interesting searches
            for (int thread = 0; thread < searchThreads.Length; thread++)
            {
                searchThreads[thread] = new ThreadAnonymousClass2(this, stopTime, totHits, totTermCount);
                searchThreads[thread].IsBackground = (true);
                searchThreads[thread].Start();
            }

            for (int thread = 0; thread < searchThreads.Length; thread++)
            {
                searchThreads[thread].Join();
            }

            if (Verbose)
            {
                Console.WriteLine("TEST: DONE search: totHits=" + totHits);
            }
        }

        private class ThreadAnonymousClass2 : ThreadJob
        {
            private readonly ThreadedIndexingAndSearchingTestCase outerInstance;

            private readonly long stopTimeMS;
            private readonly AtomicInt32 totHits;
            private readonly AtomicInt32 totTermCount;

            public ThreadAnonymousClass2(ThreadedIndexingAndSearchingTestCase outerInstance, long stopTimeMS, AtomicInt32 totHits, AtomicInt32 totTermCount)
            {
                this.outerInstance = outerInstance;
                this.stopTimeMS = stopTimeMS;
                this.totHits = totHits;
                this.totTermCount = totTermCount;
            }

            public override void Run()
            {
                if (Verbose)
                {
                    Console.WriteLine(Thread.CurrentThread.Name + ": launch search thread");
                }
                while (Environment.TickCount < stopTimeMS)
                {
                    try
                    {
                        IndexSearcher s = outerInstance.GetCurrentSearcher();
                        try
                        {
                            // Verify 1) IW is correctly setting
                            // diagnostics, and 2) segment warming for
                            // merged segments is actually happening:
                            foreach (AtomicReaderContext sub in s.IndexReader.Leaves)
                            {
                                SegmentReader segReader = (SegmentReader)sub.Reader;
                                IDictionary<string, string> diagnostics = segReader.SegmentInfo.Info.Diagnostics;
                                assertNotNull(diagnostics);
                                diagnostics.TryGetValue("source", out string source);
                                assertNotNull(source);
                                if (source.Equals("merge", StringComparison.Ordinal))
                                {
                                    assertTrue("sub reader " + sub + " wasn't warmed: warmed=" + outerInstance.warmed + " diagnostics=" + diagnostics + " si=" + segReader.SegmentInfo,
                                        // LUCENENET: ConditionalWeakTable doesn't have ContainsKey, so we normalize to TryGetValue
                                        !outerInstance.m_assertMergedSegmentsWarmed || outerInstance.warmed.TryGetValue(segReader.core, out BooleanRef _));
                                }
                            }
                            if (s.IndexReader.NumDocs > 0)
                            {
                                outerInstance.SmokeTestSearcher(s);
                                Fields fields = MultiFields.GetFields(s.IndexReader);
                                if (fields == null)
                                {
                                    continue;
                                }
                                Terms terms = fields.GetTerms("body");
                                if (terms == null)
                                {
                                    continue;
                                }
                                TermsEnum termsEnum = terms.GetEnumerator();
                                int seenTermCount = 0;
                                int shift;
                                int trigger;
                                if (totTermCount < 30)
                                {
                                    shift = 0;
                                    trigger = 1;
                                }
                                else
                                {
                                    trigger = totTermCount / 30;
                                    shift = Random.Next(trigger);
                                }
                                while (Environment.TickCount < stopTimeMS)
                                {
                                    if (!termsEnum.MoveNext())
                                    {
                                        totTermCount.Value = seenTermCount;
                                        break;
                                    }
                                    seenTermCount++;
                                    // search 30 terms
                                    if ((seenTermCount + shift) % trigger == 0)
                                    {
                                        //if (VERBOSE) {
                                        //System.out.println(Thread.currentThread().getName() + " now search body:" + term.Utf8ToString());
                                        //}
                                        totHits.AddAndGet(outerInstance.RunQuery(s, new TermQuery(new Term("body", termsEnum.Term))));
                                    }
                                }
                                //if (VERBOSE) {
                                //System.out.println(Thread.currentThread().getName() + ": search done");
                                //}
                            }
                        }
                        finally
                        {
                            outerInstance.ReleaseSearcher(s);
                        }
                    }
                    catch (Exception t)
                    {
                        Console.WriteLine(Thread.CurrentThread.Name + ": hit exc");
                        outerInstance.m_failed.Value = (true);
                        Console.WriteLine(t.ToString());
                        throw new Exception(t.ToString(), t);
                    }
                }
            }
        }

        protected virtual void DoAfterWriter(TaskScheduler es)
        {
        }

        protected virtual void DoClose()
        {
        }

        protected bool m_assertMergedSegmentsWarmed = true;

#if FEATURE_CONDITIONALWEAKTABLE_ADDORUPDATE
        private readonly ConditionalWeakTable<SegmentCoreReaders, BooleanRef> warmed = new ConditionalWeakTable<SegmentCoreReaders, BooleanRef>();
#else
        private readonly IDictionary<SegmentCoreReaders, BooleanRef> warmed = new WeakDictionary<SegmentCoreReaders, BooleanRef>().AsConcurrent();
#endif

        public virtual void RunTest(string testName)
        {
            m_failed.Value = (false);
            m_addCount.Value = 0;
            m_delCount.Value = 0;
            m_packCount.Value = 0;

            long t0 = Environment.TickCount;

            Random random = new Random(Random.Next());
            LineFileDocs docs = new LineFileDocs(random, DefaultCodecSupportsDocValues);
            DirectoryInfo tempDir = CreateTempDir(testName);
            m_dir = GetDirectory(NewMockFSDirectory(tempDir)); // some subclasses rely on this being MDW
            if (m_dir is BaseDirectoryWrapper baseDirectoryWrapper)
            {
                baseDirectoryWrapper.CheckIndexOnDispose = false; // don't double-checkIndex, we do it ourselves.
            }
            MockAnalyzer analyzer = new MockAnalyzer(LuceneTestCase.Random);
            analyzer.MaxTokenLength = TestUtil.NextInt32(LuceneTestCase.Random, 1, IndexWriter.MAX_TERM_LENGTH);
            IndexWriterConfig conf = NewIndexWriterConfig(TEST_VERSION_CURRENT, analyzer).SetInfoStream(new FailOnNonBulkMergesInfoStream());

            if (LuceneTestCase.TestNightly)
            {
                // newIWConfig makes smallish max seg size, which
                // results in tons and tons of segments for this test
                // when run nightly:
                MergePolicy mp = conf.MergePolicy;
                if (mp is TieredMergePolicy tieredMergePolicy)
                {
                    //tieredMergePolicy.MaxMergedSegmentMB = 5000.0;
                    tieredMergePolicy.MaxMergedSegmentMB = 2500.0; // LUCENENET specific - reduced each number by 50% to keep testing time under 1 hour
                }
                else if (mp is LogByteSizeMergePolicy logByteSizeMergePolicy)
                {
                    //logByteSizeMergePolicy.MaxMergeMB = 1000.0;
                    logByteSizeMergePolicy.MaxMergeMB = 500.0; // LUCENENET specific - reduced each number by 50% to keep testing time under 1 hour
                }
                else if (mp is LogMergePolicy logMergePolicy)
                {
                    //logMergePolicy.MaxMergeDocs = 100000;
                    logMergePolicy.MaxMergeDocs = 50000; // LUCENENET specific - reduced each number by 50% to keep testing time under 1 hour
                }
            }

            conf.SetMergedSegmentWarmer(new IndexReaderWarmerAnonymousClass(this));

            if (Verbose)
            {
                conf.SetInfoStream(new PrintStreamInfoStreamAnonymousClass(Console.Out));
            }
            m_writer = new IndexWriter(m_dir, conf);
            TestUtil.ReduceOpenFiles(m_writer);

            TaskScheduler es = LuceneTestCase.Random.NextBoolean() ? null : TaskScheduler.Default;

            DoAfterWriter(es);

            int NUM_INDEX_THREADS = TestUtil.NextInt32(LuceneTestCase.Random, 2, 4);

            //int RUN_TIME_SEC = LuceneTestCase.TestNightly ? 300 : RandomMultiplier;
            // LUCENENET specific - lowered from 300 to 150 to reduce total time on Nightly
            // build to less than 1 hour.
            int RUN_TIME_SEC = LuceneTestCase.TestNightly ? 150 : RandomMultiplier;

            ISet<string> delIDs = new ConcurrentHashSet<string>();
            ISet<string> delPackIDs = new ConcurrentHashSet<string>();
            ConcurrentQueue<SubDocs> allSubDocs = new ConcurrentQueue<SubDocs>();

            long stopTime = Environment.TickCount + (RUN_TIME_SEC * 1000);

            ThreadJob[] indexThreads = LaunchIndexingThreads(docs, NUM_INDEX_THREADS, stopTime, delIDs, delPackIDs, allSubDocs);

            if (Verbose)
            {
                Console.WriteLine("TEST: DONE start " + NUM_INDEX_THREADS + " indexing threads [" + (Environment.TickCount - t0) + " ms]");
            }

            // Let index build up a bit
            Thread.Sleep(100);

            DoSearching(es, stopTime);

            if (Verbose)
            {
                Console.WriteLine("TEST: all searching done [" + (Environment.TickCount - t0) + " ms]");
            }

            for (int thread = 0; thread < indexThreads.Length; thread++)
            {
                indexThreads[thread].Join();
            }

            if (Verbose)
            {
                Console.WriteLine("TEST: done join indexing threads [" + (Environment.TickCount - t0) + " ms]; addCount=" + m_addCount + " delCount=" + m_delCount);
            }

            IndexSearcher s = GetFinalSearcher();
            if (Verbose)
            {
                Console.WriteLine("TEST: finalSearcher=" + s);
            }

            assertFalse(m_failed);

            bool doFail = false;

            // Verify: make sure delIDs are in fact deleted:
            foreach (string id in delIDs)
            {
                TopDocs hits = s.Search(new TermQuery(new Term("docid", id)), 1);
                if (hits.TotalHits != 0)
                {
                    Console.WriteLine("doc id=" + id + " is supposed to be deleted, but got " + hits.TotalHits + " hits; first docID=" + hits.ScoreDocs[0].Doc);
                    doFail = true;
                }
            }

            // Verify: make sure delPackIDs are in fact deleted:
            foreach (string id in delPackIDs)
            {
                TopDocs hits = s.Search(new TermQuery(new Term("packID", id)), 1);
                if (hits.TotalHits != 0)
                {
                    Console.WriteLine("packID=" + id + " is supposed to be deleted, but got " + hits.TotalHits + " matches");
                    doFail = true;
                }
            }

            // Verify: make sure each group of sub-docs are still in docID order:
            foreach (SubDocs subDocs in allSubDocs)
            {
                TopDocs hits = s.Search(new TermQuery(new Term("packID", subDocs.PackID)), 20);
                if (!subDocs.Deleted)
                {
                    // We sort by relevance but the scores should be identical so sort falls back to by docID:
                    if (hits.TotalHits != subDocs.SubIDs.Count)
                    {
                        Console.WriteLine("packID=" + subDocs.PackID + ": expected " + subDocs.SubIDs.Count + " hits but got " + hits.TotalHits);
                        doFail = true;
                    }
                    else
                    {
                        int lastDocID = -1;
                        int startDocID = -1;
                        foreach (ScoreDoc scoreDoc in hits.ScoreDocs)
                        {
                            int docID = scoreDoc.Doc;
                            if (lastDocID != -1)
                            {
                                assertEquals(1 + lastDocID, docID);
                            }
                            else
                            {
                                startDocID = docID;
                            }
                            lastDocID = docID;
                            Document doc = s.Doc(docID);
                            assertEquals(subDocs.PackID, doc.Get("packID"));
                        }

                        lastDocID = startDocID - 1;
                        foreach (string subID in subDocs.SubIDs)
                        {
                            hits = s.Search(new TermQuery(new Term("docid", subID)), 1);
                            assertEquals(1, hits.TotalHits);
                            int docID = hits.ScoreDocs[0].Doc;
                            if (lastDocID != -1)
                            {
                                assertEquals(1 + lastDocID, docID);
                            }
                            lastDocID = docID;
                        }
                    }
                }
                else
                {
                    // Pack was deleted -- make sure its docs are
                    // deleted.  We can't verify packID is deleted
                    // because we can re-use packID for update:
                    foreach (string subID in subDocs.SubIDs)
                    {
                        assertEquals(0, s.Search(new TermQuery(new Term("docid", subID)), 1).TotalHits);
                    }
                }
            }

            // Verify: make sure all not-deleted docs are in fact
            // not deleted:
            int endID = Convert.ToInt32(docs.NextDoc().Get("docid"), CultureInfo.InvariantCulture);
            docs.Dispose();

            for (int id = 0; id < endID; id++)
            {
                string stringID = id.ToString(CultureInfo.InvariantCulture);
                if (!delIDs.Contains(stringID))
                {
                    TopDocs hits = s.Search(new TermQuery(new Term("docid", stringID)), 1);
                    if (hits.TotalHits != 1)
                    {
                        Console.WriteLine("doc id=" + stringID + " is not supposed to be deleted, but got hitCount=" + hits.TotalHits + "; delIDs=" + Collections.ToString(delIDs));
                        doFail = true;
                    }
                }
            }
            assertFalse(doFail);

            assertEquals("index=" + m_writer.SegString() + " addCount=" + m_addCount + " delCount=" + m_delCount, m_addCount - m_delCount, s.IndexReader.NumDocs);
            ReleaseSearcher(s);

            m_writer.Commit();

            assertEquals("index=" + m_writer.SegString() + " addCount=" + m_addCount + " delCount=" + m_delCount, m_addCount - m_delCount, m_writer.NumDocs);

            DoClose();
            m_writer.Dispose(false);

            // Cannot shutdown until after writer is closed because
            // writer has merged segment warmer that uses IS to run
            // searches, and that IS may be using this es!
            /*if (es != null)
            {
              es.shutdown();
              es.awaitTermination(1, TimeUnit.SECONDS);
            }*/

            TestUtil.CheckIndex(m_dir);
            m_dir.Dispose();
            //System.IO.Directory.Delete(tempDir.FullName, true);
            TestUtil.Rm(tempDir);

            if (Verbose)
            {
                Console.WriteLine("TEST: done [" + (Environment.TickCount - t0) + " ms]");
            }
        }

        private class IndexReaderWarmerAnonymousClass : IndexWriter.IndexReaderWarmer
        {
            private readonly ThreadedIndexingAndSearchingTestCase outerInstance;

            public IndexReaderWarmerAnonymousClass(ThreadedIndexingAndSearchingTestCase outerInstance)
            {
                this.outerInstance = outerInstance;
            }

            public override void Warm(AtomicReader reader)
            {
                if (Verbose)
                {
                    Console.WriteLine("TEST: now warm merged reader=" + reader);
                }
#if FEATURE_CONDITIONALWEAKTABLE_ADDORUPDATE
                outerInstance.warmed.AddOrUpdate(((SegmentReader)reader).core, true);
#else
                outerInstance.warmed[((SegmentReader)reader).core] = true;
#endif
                int maxDoc = reader.MaxDoc;
                IBits liveDocs = reader.LiveDocs;
                int sum = 0;
                int inc = Math.Max(1, maxDoc / 50);
                for (int docID = 0; docID < maxDoc; docID += inc)
                {
                    if (liveDocs == null || liveDocs.Get(docID))
                    {
                        Document doc = reader.Document(docID);
                        sum += doc.Fields.Count;
                    }
                }
                IndexSearcher searcher = 
#if FEATURE_INSTANCE_TESTDATA_INITIALIZATION
                    outerInstance.
#endif
                    NewSearcher(reader);

                sum += searcher.Search(new TermQuery(new Term("body", "united")), 10).TotalHits;

                if (Verbose)
                {
                    Console.WriteLine("TEST: warm visited " + sum + " fields");
                }
            }
        }

        private class PrintStreamInfoStreamAnonymousClass : TextWriterInfoStream
        {
            public PrintStreamInfoStreamAnonymousClass(TextWriter @out)
                : base(@out)
            {
            }

            public override void Message(string component, string message)
            {
                if ("TP".Equals(component, StringComparison.Ordinal))
                {
                    return; // ignore test points!
                }
                base.Message(component, message);
            }
        }

        // LUCENENET specific reference type of bool to mimic Java's
        // Boolean reference type.
        private class BooleanRef : IEquatable<BooleanRef>
        {
            private readonly bool value;

            public BooleanRef(bool value)
            {
                this.value = value;
            }

            public bool Equals(BooleanRef other)
            {
                return this.value.Equals(other.value);
            }

            public override bool Equals(object obj)
            {
                if (obj is BooleanRef booleanRef)
                    return Equals(booleanRef);
                if (obj is bool boolean)
                    return this.value.Equals(boolean);

                return false;
            }

            public override int GetHashCode()
            {
                return base.GetHashCode();
            }

            public static implicit operator bool(BooleanRef boolean)
            {
                return boolean.value;
            }

            public static implicit operator BooleanRef(bool boolean)
            {
                return new BooleanRef(boolean);
            }
        }

        private int RunQuery(IndexSearcher s, Query q)
        {
            s.Search(q, 10);
            int hitCount = s.Search(q, null, 10, new Sort(new SortField("title", SortFieldType.STRING))).TotalHits;
            if (DefaultCodecSupportsDocValues)
            {
                Sort dvSort = new Sort(new SortField("title", SortFieldType.STRING));
                int hitCount2 = s.Search(q, null, 10, dvSort).TotalHits;
                assertEquals(hitCount, hitCount2);
            }
            return hitCount;
        }

        protected virtual void SmokeTestSearcher(IndexSearcher s)
        {
            RunQuery(s, new TermQuery(new Term("body", "united")));
            RunQuery(s, new TermQuery(new Term("titleTokenized", "states")));
            PhraseQuery pq = new PhraseQuery();
            pq.Add(new Term("body", "united"));
            pq.Add(new Term("body", "states"));
            RunQuery(s, pq);
        }
    }
}