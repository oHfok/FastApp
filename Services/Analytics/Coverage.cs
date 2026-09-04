using System.Collections.Generic;

namespace FastApp.Services.Analytics
{
    /// <summary>
    /// What this engine actually understands, said out loud on the page.
    ///
    /// The page is careful about the claims inside a sentence and was silent
    /// about the shape of its own knowledge, which let a reader fill the gap in
    /// themselves. Somebody who reads "nothing stood out this week" without
    /// knowing what is looked for will hear "the week was unremarkable", when
    /// the truthful reading is "none of eleven specific measurements crossed a
    /// threshold". The same reader, told that the program cannot see what
    /// happens inside an application, will not take a long unbroken stretch as
    /// evidence of concentration.
    ///
    /// Kept beside the detectors on purpose. This list is a promise about what
    /// the code does, and a promise stored two directories away from the code
    /// stops being true quietly. Anything added to <see cref="Detectors"/>
    /// belongs here, and anything here that no detector implements is a lie the
    /// page is telling on the engine's behalf.
    /// </summary>
    public static class Coverage
    {
        public static readonly List<string> Understands = new()
        {
            "which application had the foreground, and for how long",
            "how often you move between applications, and where those moves go",
            "how long your unbroken stretches run, and when they start",
            "what kind of thing each application is, where you have said so",
            "which applications appear together, and which follow which",
            "what you keep returning to after looking away",
            "how any of the above compares with your own previous four weeks"
        };

        public static readonly List<string> DoesNot = new()
        {
            "what you were doing inside an application",
            "whether any of it was work, study or leisure",
            "whether a long stretch was concentration or a film left running",
            "why you moved from one thing to another",
            "whether the things you do in sequence are one task or several",
            "anything at all about the twenty-four hours before your first recorded day"
        };
    }
}
