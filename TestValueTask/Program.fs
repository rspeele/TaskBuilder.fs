/// ValueTask tests for TaskBuilder.fs
//
// Written in 2017 by Robert Peele (humbobst@gmail.com)
//
// To the extent possible under law, the author(s) have dedicated all copyright and related and neighboring rights
// to this software to the public domain worldwide. This software is distributed without any warranty.
//
// You should have received a copy of the CC0 Public Domain Dedication along with this software.
// If not, see <http://creativecommons.org/publicdomain/zero/1.0/>.

open System
open System.Collections
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks.ContextInsensitive

type IValueTaskInterface =
    abstract member TypedTask : 'a -> 'a ValueTask

let exampleValueTaskBinder (iface : IValueTaskInterface) =
    task {
        let! v1 = iface.TypedTask(3)
        let! v2 = iface.TypedTask("str")
        for i = 0 to v1 do
            printfn "looping"
            do! iface.TypedTask(())
        return v2 + string v1
    }

[<EntryPoint>]
let main argv =
    let iface =
        { new IValueTaskInterface with
            member this.TypedTask(x) = ValueTask(x)
        }
    let result = (exampleValueTaskBinder iface).Result
    printfn "%A" result
    0 // return an integer exit code
