using System.Collections.Generic;
using System.Text;

namespace GooseBrawl
{
    /// <summary>
    /// The goose's offline script: fallback lines per beat and the persona bank. This is the source of truth;
    /// Goose Brawl > Export Voice Lines writes it to backend/goose-brain/scripts/lines.json, which the Worker uses as
    /// its own fallback and the pregen script voices into Assets/Resources/GooseVoice/&lt;beat&gt;_&lt;n&gt;.wav.
    /// Picks are deterministic (round + beat index) so the one demo run never depends on a dice roll.
    /// </summary>
    public static class GooseLines
    {
        public enum Beat { Intro, Taunt10, Yell, Bread, Dodge, Rage, Caught, Outlasted, IntroAgain }

        public static readonly string[] Keys = { "intro", "taunt10", "yell", "bread", "dodge", "rage", "caught", "outlasted", "intro_again" };

        public static string Key(Beat b) => Keys[(int)b];

        /// <summary>Each persona owns a voice for good: the male names speak with the male stock voice, the female names with the female one.</summary>
        public static readonly (string name, string title, bool female)[] Personas =
        {
            ("KEVIN", "DESTROYER OF BREAKFAST", false),
            ("BRENDA", "SHE KNOWS WHAT YOU DID", true),
            ("GARY", "JUST GARY.", false),
            ("MARGARET", "MOTHER OF ONE. FORMERLY.", true),
            ("DR. HONK", "PHD IN PAIN", false),
            ("AGNES", "BANNED FROM THREE PARKS", true),
            ("LORD FEATHERINGTON", "OF THE POND", false),
            ("PAMELA", "YOUR LANDLORD NOW", true),
            ("STEVE", "LOCAL MENACE", false),
            ("CHAD", "HAS NEVER LOST", false),
            ("DUKE", "THE FLAPPENING", false),
            ("BARRY", "HONK FIRST, ASK NEVER", false),
        };

        public const string MaleVoiceFolder = "m";
        public const string FemaleVoiceFolder = "f";

        public static readonly Dictionary<Beat, string[]> Lines = new Dictionary<Beat, string[]>
        {
            { Beat.Intro, new[] {
                "MY EGG. MY FLOOR. YOUR PROBLEM.",
                "[sighs] YOU DROPPED IT. YOU DROPPED IT.",
                "BREAKFAST? I'LL SHOW YOU BREAKFAST.",
                "I FLEW HERE FOR THIS. FOR YOU. RUN.",
                "THAT WAS MY CHILD. PROBABLY. RUN.",
                "NICE ROOM. SHAME ABOUT WHAT HAPPENS NEXT.",
                "[whispers] I CAN SMELL THE YOLK ON YOU.",
                "HELLO. I AM THE CONSEQUENCES.",
                "YOU HAVE LEGS. USE THEM. I'M COUNTING.",
                "ONE EGG. ONE CHASE. NO REFUNDS." } },
            { Beat.IntroAgain, new[] {
                "OH. IT'S YOU AGAIN. RUN FASTER THIS TIME.",
                "YOU AGAIN. NOT FORGIVEN. NOT FORGOTTEN.",
                "[sighs] BACK FOR MORE. HOW BRAVE. HOW DUMB.",
                "WE MEET AGAIN. STILL MAD, BY THE WAY.",
                "I REMEMBER YOU. I REMEMBER EVERYTHING.",
                "SAME EGG. SAME YOU. SAME ENDING.",
                "[laughs] YOU CAME BACK. TO ME. ADORABLE.",
                "THE GRUDGE IS RESTED AND READY. ARE YOU?" } },
            { Beat.Taunt10, new[] {
                "TEN SECONDS. I'VE SEEN BREAD LAST LONGER.",
                "[laughs] IS THAT YOUR RUNNING? ADORABLE.",
                "I HAVE ALL DAY. YOU HAVE LEGS. BARELY.",
                "YOUR SHOES ARE UNTIED. MADE YOU LOOK.",
                "TEN SECONDS. THE EGG LASTED LONGER.",
                "KEEP GOING. I LIKE THE CHASE. YOU DON'T.",
                "[sarcastic] WOW. SO FAST. TRULY A GAZELLE.",
                "YOU RUN LIKE THE EGG ROLLED. BADLY.",
                "IS THIS A JOG? I THOUGHT IT WAS A CHASE.",
                "I'M NOT EVEN FLAPPING YET." } },
            { Beat.Yell, new[] {
                "[sarcastic] OH NO. WORDS. I'M SHAKING.",
                "RUDE. I ONLY SPEAK GOOSE. LOUDER.",
                "YELL ALL YOU WANT. THE EGG IS STILL BROKEN.",
                "WAS THAT A THREAT? FROM A PERSON? CUTE.",
                "I HEARD THAT. THE WHOLE POND HEARD THAT.",
                "[laughs] YOU YELLED. I FLINCHED. NEVER AGAIN.",
                "USE YOUR INSIDE VOICE. I'M INSIDE. I DON'T CARE.",
                "NOISE DOESN'T FIX EGGS. I CHECKED.",
                "BIG VOICE. SMALL LEGS. I'VE NOTICED.",
                "OKAY. NOW I'M YELLING TOO. HONK." } },
            { Beat.Bread, new[] {
                "CARBS. NICE TRY. STILL FURIOUS.",
                "[excited] BREAD! ...WAIT. THIS CHANGES NOTHING.",
                "MMM. CRUSTY. NOW WHERE WAS I. OH RIGHT. YOU.",
                "YOU THINK I CAN BE BOUGHT? ...CONTINUE.",
                "A ROLL. FOR AN EGG. THE MATH IS INSULTING.",
                "DELICIOUS. IRRELEVANT. RUN.",
                "[sighs] FINE. I ATE IT. I'M NOT PROUD.",
                "BRIBERY. NOTED. ALSO, MORE PLEASE. LATER.",
                "THAT WAS THE APPETIZER. YOU'RE THE MAIN.",
                "I'M FULL OF BREAD AND RAGE NOW. THANKS." } },
            { Beat.Dodge, new[] {
                "LUCKY. THAT WAS A PRACTICE LUNGE.",
                "[sighs] FINE. I MISSED. IT WON'T HAPPEN TWICE.",
                "OH, YOU CAN MOVE. GOOD. MORE FUN FOR ME.",
                "I MEANT TO DO THAT. FOR SUSPENSE.",
                "THE FLOOR MOVED. I'M SURE OF IT.",
                "[laughs] CUTE STEP. I'VE GOT NINETY MORE LUNGES.",
                "DODGED? OR JUST FELL OVER GRACEFULLY?",
                "THAT ONE WAS FOR THE CAMERA. THIS ONE ISN'T.",
                "YOU MOVED. HOW DARE YOU.",
                "NOTED. NEXT TIME I AIM FOR THE SHOES." } },
            { Beat.Rage, new[] {
                "THAT'S IT. NO MORE MISTER NICE GOOSE.",
                "RAGE MODE. THAT IS NOT A METAPHOR.",
                "I AM THE STORM. THE STORM HAS FEATHERS.",
                "THIRTY SECONDS OF PATIENCE. IT'S GONE.",
                "I HAVE CALLED MY COUSINS. THEY'RE WORSE.",
                "YOU DID THIS. REMEMBER THAT.",
                "[laughs] NO MORE WARM-UP. THIS IS THE ACTUAL GOOSE.",
                "FEATHERS UP. MANNERS DOWN." } },
            { Beat.Caught, new[] {
                "[laughs] GOT YOU. TELL YOUR FRIENDS.",
                "BREAKFAST IS CANCELLED. FOREVER.",
                "SAY IT. SAY I'M THE BEST GOOSE. SAY IT.",
                "AND THAT'S HOW EGGS ARE AVENGED.",
                "YOU RAN. I FLEW. THAT'S THE WHOLE STORY.",
                "[sighs] SO EASY. I ALMOST FEEL BAD. ALMOST.",
                "TAG. YOU'RE BREAKFAST.",
                "GOOSE ONE. YOU ZERO. AGAIN.",
                "THIS IS WHAT HAPPENS. THIS IS ALWAYS WHAT HAPPENS.",
                "I'D SAY GOOD GAME. I WON'T." } },
            { Beat.Outlasted, new[] {
                "[sighs] FINE. KEEP YOUR STUPID LEGS.",
                "I LET YOU WIN. OBVIOUSLY. I'M TIRED, THAT'S ALL.",
                "THIS ISN'T OVER. I KNOW WHERE YOU LIVE. IT'S HERE.",
                "MY KNEES. YOU HAVE NO IDEA. NEXT TIME.",
                "[laughs] THAT DOESN'T COUNT. I WASN'T READY.",
                "CONGRATULATIONS. YOU OUTRAN A BIRD. FRAME IT.",
                "I QUIT. FOR NOW. THE EGG STILL COUNTS.",
                "GO. ENJOY YOUR LEGS. I'LL BE HERE." } },
        };

        /// <summary>
        /// Deterministic line index: the round spreads the picks, the beat offsets them, and the k-th repeat of a beat
        /// within a round (second dodge, second shout) moves to the next line. No dice anywhere.
        /// </summary>
        public static int Index(Beat beat, int round, int repeat = 0)
        {
            var arr = Lines[beat];
            int i = (round * 3 + (int)beat + repeat) % arr.Length;
            return i < 0 ? i + arr.Length : i;
        }

        public static string Pick(Beat beat, int round, int repeat = 0) => Lines[beat][Index(beat, round, repeat)];

        public static (string name, string title, bool female) PickPersona(int gamesPlayed)
        {
            int i = gamesPlayed % Personas.Length;
            return Personas[i < 0 ? i + Personas.Length : i];
        }

        /// <summary>Text without the ElevenLabs audio tags, for the HUD.</summary>
        public static string Display(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";
            var sb = new StringBuilder(line.Length);
            bool inTag = false;
            foreach (char c in line)
            {
                if (c == '[') { inTag = true; continue; }
                if (c == ']') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString().Replace("  ", " ").Trim();
        }

        public static string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"personas\": [\n");
            for (int i = 0; i < Personas.Length; i++)
                sb.Append("    { \"name\": ").Append(Q(Personas[i].name)).Append(", \"title\": ").Append(Q(Personas[i].title)).Append(", \"voice\": ").Append(Q(Personas[i].female ? "female" : "male")).Append(" }").Append(i < Personas.Length - 1 ? ",\n" : "\n");
            sb.Append("  ],\n  \"beats\": {\n");
            int b = 0;
            foreach (var kv in Lines)
            {
                sb.Append("    ").Append(Q(Key(kv.Key))).Append(": [\n");
                for (int i = 0; i < kv.Value.Length; i++) sb.Append("      ").Append(Q(kv.Value[i])).Append(i < kv.Value.Length - 1 ? ",\n" : "\n");
                sb.Append("    ]").Append(++b < Lines.Count ? ",\n" : "\n");
            }
            sb.Append("  }\n}\n");
            return sb.ToString();
        }

        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
