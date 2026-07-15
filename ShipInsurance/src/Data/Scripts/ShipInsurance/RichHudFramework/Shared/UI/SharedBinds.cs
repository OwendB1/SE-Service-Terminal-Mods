using RichHudFramework.Internal;

namespace RichHudFramework.UI
{
    using Client;
    using Server;

    /// <summary>
    /// A collection of immutable, commonly used key binds used by the framework library.
    /// </summary>
    public sealed class SharedBinds : RichHudComponentBase
    {
        public static IBind LeftButton => Instance.sharedMain[0];
        public static IBind RightButton => Instance.sharedMain[1];
        public static IBind MousewheelUp => Instance.sharedMain[2];
        public static IBind MousewheelDown => Instance.sharedMain[3];

        public static IBind Enter => Instance.sharedMain[4];
        public static IBind Back => Instance.sharedMain[5];
        public static IBind Delete => Instance.sharedMain[6];
        public static IBind Escape => Instance.sharedMain[7];

        public static IBind SelectAll => Instance.sharedMain[8];
        public static IBind Copy => Instance.sharedMain[9];
        public static IBind Cut => Instance.sharedMain[10];
        public static IBind Paste => Instance.sharedMain[11];

        public static IBind UpArrow => Instance.sharedMain[12];
        public static IBind DownArrow => Instance.sharedMain[13];
        public static IBind LeftArrow => Instance.sharedMain[14];
        public static IBind RightArrow => Instance.sharedMain[15];

        public static IBind PageUp => Instance.sharedMain[16];
        public static IBind PageDown => Instance.sharedMain[17];
        public static IBind Space => Instance.sharedMain[18];

        public static IBind RightStickX => Instance.sharedMain[19];
        public static IBind RightStickY => Instance.sharedMain[20];

        public static IBind Shift => Instance.sharedModifiers[0];
        public static IBind Control => Instance.sharedModifiers[1];
        public static IBind Alt => Instance.sharedModifiers[2];

        private static SharedBinds Instance
        {
            get { Init(); return instance; }
            set { instance = value; }
        }
        private static SharedBinds instance;
        private readonly IBindGroup sharedMain, sharedModifiers;

        private SharedBinds() : base(false, true)
        {
            sharedMain = BindManager.GetOrCreateGroup("SharedBinds");
            sharedMain.RegisterBinds(new BindGroupInitializer
            {
                { "leftbutton", RichHudControls.LeftButton, new KeyComboInit { RichHudControls.LeftBumper } },
                { "rightbutton", RichHudControls.RightButton, new KeyComboInit { RichHudControls.RightBumper } },
                { "mousewheelup", RichHudControls.MousewheelUp },
                { "mousewheeldown", RichHudControls.MousewheelDown },

                { "enter", RichHudControls.Enter },
                { "back", RichHudControls.Back },
                { "delete", RichHudControls.Delete },
                { "escape", RichHudControls.Escape, new KeyComboInit { RichHudControls.GpadB }  },

                { "selectall", RichHudControls.Control, RichHudControls.A },
                { "copy", RichHudControls.Control, RichHudControls.C },
                { "cut", RichHudControls.Control, RichHudControls.X },
                { "paste", RichHudControls.Control, RichHudControls.V },

                { "uparrow", RichHudControls.Up },
                { "downarrow", RichHudControls.Down },
                { "leftarrow", RichHudControls.Left },
                { "rightarrow", RichHudControls.Right },
                
                { "pageup", RichHudControls.PageUp },
                { "pagedown", RichHudControls.PageDown },
                { "space", RichHudControls.Space },

                { "rightstickx", RichHudControls.RightStickX },
                { "rightsticky", RichHudControls.RightStickY },
            });
            sharedModifiers = BindManager.GetOrCreateGroup("SharedModifiers");
            sharedModifiers.RegisterBinds(new BindGroupInitializer
            {
                { "shift", RichHudControls.Shift },
                { "control", RichHudControls.Control },
                { "alt", RichHudControls.Alt },
            });
        }

        private static void Init()
        {
            if (instance == null)
                instance = new SharedBinds();
        }

        /// <summary>
        /// Internal component closing callback
        /// </summary>
        /// <exclude/>
        public override void Close()
        {
            Instance = null;
        }
    }
}