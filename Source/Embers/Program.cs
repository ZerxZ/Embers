﻿using System;
using System.Diagnostics;
using static Embers.Script;

namespace Embers
{
    internal class Program
    {
        static void Main() {
            // Test
            {
                Interpreter Interpreter = new();
                Script Script = new(Interpreter);
                Benchmark(() =>
                    Script.Evaluate(@"
#for i in 1..1_000_000
    
#end

class Z
    @a = ""hi""
    @@b = ""hey""
end
p Z.instance_variables
p Z.class_variables
p Z.new.instance_variables
p defined? Z.new.class_variables

p :hi.object_id
p :hey.object_id
p :hi.object_id

5.times do
    p 1
end

for i in 1..100_000
    i.to_s.to_sym
end
                    ")
                );
                Console.ReadLine();
            }
            // Benchmark
            {
                Interpreter Interpreter = new();
                Script Script = new(Interpreter);
                Benchmark(() =>
                    Script.Evaluate("1_000_000.times do end")
                );
                Console.ReadLine();
            }
        }
        static void Benchmark(Action Code, int Times = 1) {
            Stopwatch Stopwatch = new();
            Stopwatch.Start();
            for (int i = 0; i < Times; i++)
                Code();
            Stopwatch.Stop();
            Console.WriteLine($"Took {Stopwatch.ElapsedMilliseconds / 1000d} seconds");
        }
    }
}