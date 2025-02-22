﻿/*
 * Original author: Nicholas Shulman <nicksh .at. u.washington.edu>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2011 University of Washington - Seattle, WA
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using pwiz.Common.DataBinding.Controls.Editor;

namespace pwiz.Topograph.Test
{
    /// <summary>
    /// Summary description for ListViewManagerTest
    /// </summary>
    [TestClass]
    public class ListViewHelperTest
    {
        [TestMethod]
        public void TestMoveItems()
        {
            Assert.IsTrue(new[] {1,3,2}
                .SequenceEqual(ListViewHelper.MoveItems(
                    Enumerable.Range(1,3), new[]{2}, true)));
            Assert.IsTrue(new[] {2,1,3}
                .SequenceEqual(ListViewHelper
                .MoveItems(Enumerable.Range(1,3), new[]{0}, false)));
        }
    }
}
