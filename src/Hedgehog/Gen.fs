﻿namespace Hedgehog

open System
open Hedgehog.Numeric

/// A generator for values and shrink trees of type 'a.
[<Struct>]
type Gen<'a> =
    | Gen of (Seed -> Size -> Tree<'a>)

module Gen =

    let private unsafeRun (seed : Seed) (size : Size) (Gen g : Gen<'a>) : Tree<'a> =
        g seed size

    let run (seed : Seed) (size : Size) (g : Gen<'a>) : Tree<'a> =
        unsafeRun seed (max 1 size) g

    let extract (seed : Seed) (size : Size) (g : Gen<'a>) : 'a =
        run seed size g
        |> Tree.outcome

    let delay (f : unit -> Gen<'a>) : Gen<'a> =
        Gen (fun seed size ->
            unsafeRun seed size (f ()))

    let tryFinally (m : Gen<'a>) (after : unit -> unit) : Gen<'a> =
        Gen (fun seed size ->
            try
                unsafeRun seed size m
            finally
                after ())

    let tryWith (m : Gen<'a>) (k : exn -> Gen<'a>) : Gen<'a> =
        Gen (fun seed size ->
            try
                unsafeRun seed size m
            with
                x -> unsafeRun seed size (k x))

    let create (shrink : 'a -> seq<'a>) (r : Seed -> Size -> 'a) : Gen<'a> =
        Gen (fun seed size ->
            Tree.unfold id shrink (r seed size))

    let constant (x : 'a) : Gen<'a> =
        Gen (fun _ _ ->
            Tree.singleton x)

    let bind (m0 : Gen<'a>) (k0 : 'a -> Gen<'b>) : Gen<'b> =
        Gen (fun seed0 size ->
            let seed1, seed2 =
                Seed.split seed0

            Tree.bind (run seed1 size m0) (run seed2 size << k0))

    let replicate (times : int) (g : Gen<'a>) : Gen<'a list> =
        let rec loop n xs =
            if n <= 0 then
                constant xs
            else
                bind g (fun x -> loop (n - 1) (x :: xs))
        loop times []

    let apply (gf : Gen<'a -> 'b>) (gx : Gen<'a>) : Gen<'b> =
        bind gf <| fun f ->
        bind gx <| (f >> constant)

    let mapTree (f : Tree<'a> -> Tree<'b>) (g : Gen<'a>) : Gen<'b> =
        Gen (fun seed size ->
            f (unsafeRun seed size g))

    let map (f : 'a -> 'b) (g : Gen<'a>) : Gen<'b> =
        mapTree (Tree.map f) g

    let map2 (f : 'a -> 'b -> 'c) (gx : Gen<'a>) (gy : Gen<'b>) : Gen<'c> =
        bind gx <| fun x ->
        bind gy <| fun y ->
        constant (f x y)

    let map3 (f : 'a -> 'b -> 'c -> 'd) (gx : Gen<'a>) (gy : Gen<'b>) (gz : Gen<'c>) : Gen<'d> =
        bind gx <| fun x ->
        bind gy <| fun y ->
        bind gz <| fun z ->
        constant (f x y z)

    let map4 (f : 'a -> 'b -> 'c -> 'd -> 'e) (gx : Gen<'a>) (gy : Gen<'b>) (gz : Gen<'c>) (gw : Gen<'d>) : Gen<'e> =
        bind gx <| fun x ->
        bind gy <| fun y ->
        bind gz <| fun z ->
        bind gw <| fun w ->
        constant (f x y z w)

    let zip (gx : Gen<'a>) (gy : Gen<'b>) : Gen<'a * 'b> =
        map2 (fun x y -> x, y) gx gy

    let zip3 (gx : Gen<'a>) (gy : Gen<'b>) (gz : Gen<'c>) : Gen<'a * 'b * 'c> =
        map3 (fun x y z -> x, y, z) gx gy gz

    let zip4 (gx : Gen<'a>) (gy : Gen<'b>) (gz : Gen<'c>) (gw : Gen<'d>) : Gen<'a * 'b * 'c * 'd> =
        map4 (fun x y z w -> x, y, z, w) gx gy gz gw

    let tuple  (g : Gen<'a>) : Gen<'a * 'a> =
        zip g g

    let tuple3 (g : Gen<'a>) : Gen<'a * 'a * 'a> =
        zip3 g g g

    let tuple4 (g : Gen<'a>) : Gen<'a * 'a * 'a * 'a> =
        zip4 g g g g

    type Builder internal () =
        let rec loop p m =
            if p () then
                bind m (fun _ -> loop p m)
            else
                constant ()

        member __.Return(a) =
            constant a
        member __.ReturnFrom(g) =
            g
        member __.Bind(m, k) =
            bind m k
        member __.For(xs, k) =
            let xse = (xs :> seq<'a>).GetEnumerator ()
            using xse <| fun xse ->
                let mv = xse.MoveNext
                let kc = delay (fun () -> k xse.Current)
                loop mv kc
        member __.Combine(m, n) =
            bind m (fun () -> n)
        member __.Delay(f) =
            delay f
        member __.Zero() =
            constant ()

    let private gen = Builder ()

    //
    // Combinators - Shrinking
    //

    /// Prevent a 'Gen' from shrinking.
    let noShrink (g : Gen<'a>) : Gen<'a> =
        let drop (Node (x, _)) =
            Node (x, Seq.empty)
        mapTree drop g

    /// Apply an additional shrinker to all generated trees.
    let shrinkLazy (f : 'a -> seq<'a>) (g : Gen<'a>) : Gen<'a> =
        mapTree (Tree.expand f) g

    /// Apply an additional shrinker to all generated trees.
    let shrink (f : 'a -> List<'a>) (g : Gen<'a>) : Gen<'a>  =
        shrinkLazy (Seq.ofList << f) g

    //
    // Combinators - Size
    //

    /// Used to construct generators that depend on the size parameter.
    let sized (f : Size -> Gen<'a>) : Gen<'a> =
        Gen (fun seed size ->
            unsafeRun seed size (f size))

    /// Overrides the size parameter. Returns a generator which uses the
    /// given size instead of the runtime-size parameter.
    let resize (n : int) (g : Gen<'a>) : Gen<'a> =
        Gen (fun seed _ ->
            run seed n g)

    /// Adjust the size parameter, by transforming it with the given
    /// function.
    let scale (f : int -> int) (g : Gen<'a>) : Gen<'a> =
        sized <| fun n ->
            resize (f n) g

    //
    // Combinators - Numeric
    //

    /// Generates a random number in the given inclusive range.
    let inline integral (range : Range<'a>) : Gen<'a> =
        let randomIntegral seed size =
            let (lo, hi) = Range.bounds size range
            let x, _ = Seed.nextBigInt (toBigInt lo) (toBigInt hi) seed
            fromBigInt x
        create (Shrink.towards <| Range.origin range) randomIntegral

    //
    // Combinators - Choice
    //

    let private crashEmpty (arg : string) : 'b =
        invalidArg arg (sprintf "'%s' must have at least one element" arg)

    /// Randomly selects one of the values in the list.
    /// <i>The input list must be non-empty.</i>
    let item (xs0 : seq<'a>) : Gen<'a> = gen {
        let xs = Array.ofSeq xs0
        if Array.isEmpty xs then
            return crashEmpty "xs"
        else
            let! ix = integral <| Range.constant 0 (Array.length xs - 1)
            return Array.item ix xs
    }

    /// Uses a weighted distribution to randomly select one of the gens in the list.
    /// This generator shrinks towards the first generator in the list.
    /// <i>The input list must be non-empty.</i>
    let frequency (xs0 : seq<int * Gen<'a>>) : Gen<'a> = gen {
        let xs =
            List.ofSeq xs0

        let total =
            List.sumBy fst xs

        let rec pick n = function
            | [] ->
                crashEmpty "xs"
            | (k, y) :: ys ->
                if n <= k then
                    y
                else
                    pick (n - k) ys

        let! n = integral <| Range.constant 1 total
        return! pick n xs
    }

    /// Randomly selects one of the gens in the list.
    /// <i>The input list must be non-empty.</i>
    let choice (xs0 : seq<Gen<'a>>) : Gen<'a> = gen {
        let xs = Array.ofSeq xs0
        if Array.isEmpty xs then
            return crashEmpty "xs" xs
        else
            let! ix = integral <| Range.constant 0 (Array.length xs - 1)
            return! Array.item ix xs
    }

    /// Randomly selects from one of the gens in either the non-recursive or the
    /// recursive list. When a selection is made from the recursive list, the size
    /// is halved. When the size gets to one or less, selections are no longer made
    /// from the recursive list.
    /// <i>The first argument (i.e. the non-recursive input list) must be non-empty.</i>
    let choiceRec (nonrecs : seq<Gen<'a>>) (recs : seq<Gen<'a>>) : Gen<'a> =
        sized <| fun n ->
            if n <= 1 then
                choice nonrecs
            else
                let halve x = x / 2
                choice <| Seq.append nonrecs (Seq.map (scale halve) recs)

    //
    // Combinators - Conditional
    //

    /// Tries to generate a value that satisfies a predicate.
    let tryFilter (p : 'a -> bool) (g : Gen<'a>) : Gen<'a option> =
        let rec tryN k = function
            | 0 ->
                constant None
            | n ->
                let g' = resize (2 * k + n) g
                bind g' <| fun x ->
                    if p x then
                        constant (Some x)
                    else
                        tryN (k + 1) (n - 1)

        sized (tryN 0 << max 1)

    /// Generates a value that satisfies a predicate.
    let filter (p : 'a -> bool) (g : Gen<'a>) : Gen<'a> =
        let rec loop () =
            bind (tryFilter p g) <| function
                | None ->
                    sized <| fun n ->
                        resize (n + 1) (delay loop)
                | Some x ->
                    constant x

        loop ()

    /// Runs an option generator until it produces a 'Some'.
    let some (g : Gen<'a option>) : Gen<'a> =
        bind (filter Option.isSome g) <| function
        | Some x ->
            constant x
        | None ->
            invalidOp "internal error, unexpected None"

    //
    // Combinators - Collections
    //

    /// Generates a 'None' part of the time.
    let option (g : Gen<'a>) : Gen<'a option> =
        sized <| fun n ->
            frequency [
                2, constant None
                1 + n, map Some g
            ]

    /// Generates a list using a 'Range' to determine the length.
    let list (range : Range<int>) (g : Gen<'a>) : Gen<List<'a>> =
        bind (integral range) <| fun n ->
            replicate n g

    /// Generates an array using a 'Range' to determine the length.
    let array (range : Range<int>) (g : Gen<'a>) : Gen<array<'a>> =
        list range g |> map Array.ofList

    /// Generates a sequence using a 'Range' to determine the length.
    let seq (range : Range<int>) (g : Gen<'a>) : Gen<seq<'a>> =
        list range g |> map Seq.ofList

    //
    // Combinators - Characters
    //

    // Generates a random character in the specified range.
    let char (lo : char) (hi : char) : Gen<char> =
        integral <| Range.constant (int lo) (int hi) |> map char

    /// Generates a Unicode character, including invalid standalone surrogates:
    /// '\000'..'\65535'
    let unicodeAll : Gen<char> =
        let lo = Char.MinValue
        let hi = Char.MaxValue
        char lo hi

    // Generates a random digit.
    let digit : Gen<char> =
        char '0' '9'

    // Generates a random lowercase character.
    let lower : Gen<char> =
        char 'a' 'z'

    // Generates a random uppercase character.
    let upper : Gen<char> =
        char 'A' 'Z'

    /// Generates an ASCII character: '\000'..'\127'
    let ascii : Gen<char> =
        char '\000' '\127'

    /// Generates a Latin-1 character: '\000'..'\255'
    let latin1 : Gen<char> =
        char '\000' '\255'

    /// Generates a Unicode character, excluding noncharacters
    /// ('\65534', '\65535') and invalid standalone surrogates
    /// ('\000'..'\65535' excluding '\55296'..'\57343').
    let unicode : Gen<char> =
        let isNoncharacter x =
               x = Operators.char 65534
            || x = Operators.char 65535
        unicodeAll
        |> filter (not << isNoncharacter)
        |> filter (not << Char.IsSurrogate)

    // Generates a random alpha character.
    let alpha : Gen<char> =
        choice [lower; upper]

    // Generates a random alpha-numeric character.
    let alphaNum : Gen<char> =
        choice [lower; upper; digit]

    /// Generates a random string using 'Range' to determine the length and the
    /// specified character generator.
    let string (range : Range<int>) (g : Gen<char>) : Gen<string> =
        sized (fun _ -> array range g)
        |> map String

    //
    // Combinators - Primitives
    //

    /// Generates a random boolean.
    let bool : Gen<bool> =
        item [false; true]

    /// Generates a random byte.
    let byte (range : Range<byte>) : Gen<byte> =
        integral range

    /// Generates a random signed byte.
    let sbyte (range : Range<sbyte>) : Gen<sbyte> =
        integral range

    /// Generates a random signed 16-bit integer.
    let int16 (range : Range<int16>) : Gen<int16> =
        integral range

    /// Generates a random unsigned 16-bit integer.
    let uint16 (range : Range<uint16>) : Gen<uint16> =
        integral range

    /// Generates a random signed 32-bit integer.
    let int (range : Range<int>) : Gen<int> =
        integral range

    /// Generates a random unsigned 32-bit integer.
    let uint32 (range : Range<uint32>) : Gen<uint32> =
        integral range

    /// Generates a random signed 64-bit integer.
    let int64 (range : Range<int64>) : Gen<int64> =
        integral range

    /// Generates a random unsigned 64-bit integer.
    let uint64 (range : Range<uint64>) : Gen<uint64> =
        integral range

    /// Generates a random 64-bit floating point number.
    let double (range : Range<double>) : Gen<double> =
        let randomDouble seed size =
            let (lo, hi) = Range.bounds size range
            let x, _ = Seed.nextDouble lo hi seed
            x
        create (Shrink.towardsDouble <| Range.origin range) randomDouble

    /// Generates a random 64-bit floating point number.
    let float (range : Range<float>) : Gen<float> =
        (double range) |> map float

    /// Generates a random 32-bit floating point number.
    let single (range : Range<single>) : Gen<single> =
      double (Range.map ExtraTopLevelOperators.double range) |> map single

    /// Generates a random decimal floating-point number.
    let decimal (range : Range<decimal>) : Gen<decimal> =
      double (Range.map ExtraTopLevelOperators.double range) |> map decimal

    //
    // Combinators - Constructed
    //

    /// Generates a random globally unique identifier.
    let guid : Gen<Guid> = gen {
        let! bs = array (Range.constant 16 16) (byte <| Range.constantBounded ())
        return Guid bs
    }

    /// Generates a random DateTime using the specified range.
    /// For example:
    ///   let range =
    ///      Range.constantFrom
    ///          (DateTime (2000, 1, 1)) DateTime.MinValue DateTime.MaxValue
    ///   Gen.dateTime range
    let dateTime (range : Range<DateTime>) : Gen<DateTime> =
        gen {
            let! ticks = range |> Range.map (fun dt -> dt.Ticks) |> integral
            return DateTime ticks
        }

    /// Generates a random DateTimeOffset using the specified range.
    let dateTimeOffset (range : Range<DateTimeOffset>) : Gen<DateTimeOffset> =
        gen {
            let! ticks = range |> Range.map (fun dt -> dt.Ticks) |> integral
            // Ensure there is no overflow near the edges when adding the offset
            let minOffsetMinutes =
              max
                (-14L * 60L)
                ((DateTimeOffset.MaxValue.Ticks - ticks) / TimeSpan.TicksPerMinute * -1L)
            let maxOffsetMinutes =
              min
                (14L * 60L)
                ((ticks - DateTimeOffset.MinValue.Ticks) / TimeSpan.TicksPerMinute)
            let! offsetMinutes = int (Range.linearFrom 0 (Operators.int minOffsetMinutes) (Operators.int maxOffsetMinutes))
            return DateTimeOffset(ticks, TimeSpan.FromMinutes (Operators.float offsetMinutes))
        }

    //
    // Sampling
    //

    let sampleTree (size : Size) (count : int) (g : Gen<'a>) : List<Tree<'a>> =
        let action seed _ =
            let (seed1, seed2) = Seed.split seed
            (run seed1 size g), seed2

        let seed = Seed.random ()

        Seq.init count id
        |> Seq.mapFold action seed
        |> fst
        |> Seq.toList

    let sample (size : Size) (count : int) (g : Gen<'a>) : List<'a> =
        sampleTree size count g
        |> List.map Tree.outcome

    /// Run a generator. The size passed to the generator is always 30;
    /// if you want another size then you should explicitly use 'resize'.
    let generateTree (g : Gen<'a>) : Tree<'a> =
        run (Seed.random ()) 30 g

    let printSample (g : Gen<'a>) : unit =
        let forest = sampleTree 10 5 g
        for tree in forest do
            printfn "=== Outcome ==="
            printfn "%A" <| Tree.outcome tree
            printfn "=== Shrinks ==="
            for shrink in Tree.shrinks tree do
                printfn "%A" <| Tree.outcome shrink
            printfn "."

[<AutoOpen>]
module GenBuilder =
    let gen = Gen.Builder ()

[<AutoOpen>]
module GenOperators =
    let (<!>) = Gen.map
    let (<*>) = Gen.apply
