using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using StardewValley.BellsAndWhistles;
using System.Threading.Tasks;



namespace StardewGPT
{
    internal sealed class GptMenu: IClickableMenu
    {

        private RAG rag;

        private bool _isSending = false;

        private readonly List<ChatMessage> history;
        private readonly TextBox input;
        private readonly ClickableComponent inputComponent;
        private readonly ClickableComponent sendButton;


        private Rectangle logPanel;
        private const int HeaderPaddingPx = 4; // Space between chat's role and message
        private const int MessagePaddingPx = 14; // space between each chat message chunk

        private const int OuterPaddingPx = 24;  // left/right padding from menu frame
        private const int TopInsetPx = 64;      // space below top border/title
        private const int InputHeightPx = 64;   // matches TextBox height
        private const int SendWidthPx = 96;
        private const int FooterHeightPx = 36;  // vertical space reserved for footer line
        private const int GapPx = 12;           // vertical gap between sections


        private int scrollOffsetPx = 0;   // how far we scrolled down, in pixels
        private int contentHeightPx = 0;  // total height of all chat content, in pixels
        private const int ScrollStepPx = 60; // how much one wheel notch scrolls

        public static int menuWidth = 632 + borderWidth * 2;
        public static int menuHeight = 600 + borderWidth * 2 + Game1.tileSize;



        public GptMenu(List<ChatMessage> sharedHistory, RAG rag):base(0,0,menuWidth,menuHeight,true)
        {
            this.rag = rag;

            this.history = sharedHistory;
            this.xPositionOnScreen = (Game1.viewport.Width - menuWidth) /2 ;
            this.yPositionOnScreen = (Game1.viewport.Height - menuHeight)/2;
            // close X (top-right)
            this.upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(this.xPositionOnScreen + this.width - 36 - 12, this.yPositionOnScreen + 12, 36, 36),
                Game1.mouseCursors,
                new Rectangle(337, 494, 12, 12),
                3f
            );

            int left = this.xPositionOnScreen + OuterPaddingPx;
            int right = this.xPositionOnScreen + this.width - OuterPaddingPx;
            int innerWidth = this.width - OuterPaddingPx * 2;

            // footer baseline (where the footer text sits)
            int footerY = this.yPositionOnScreen + this.height - FooterHeightPx;

            // input row is directly above footer
            int inputY = footerY - GapPx - InputHeightPx;

            // log panel fills the space from top inset down to above input row
            int logTop = this.yPositionOnScreen + TopInsetPx;
            int logBottom = inputY - GapPx;
            int logHeight = logBottom - logTop;

            // log panel (now dynamic height)
            this.logPanel = new Rectangle(
                left,
                logTop,
                innerWidth,
                logHeight
            );

            // input textbox
            this.input = new TextBox(
                Game1.content.Load<Texture2D>("LooseSprites\\textBox"),
                null,
                Game1.smallFont,
                Game1.textColor
            )
            {
                X = left,
                Y = inputY,
                Width = innerWidth - SendWidthPx - GapPx,
                Text = ""
            };

            this.inputComponent = new ClickableComponent(
                new Rectangle(this.input.X, this.input.Y, this.input.Width, InputHeightPx),
                "InputBox"
            );

            // send button
            Rectangle sendBounds = new Rectangle(
                right - SendWidthPx,
                inputY,
                SendWidthPx,
                InputHeightPx
            );

            this.sendButton = new ClickableComponent(sendBounds, "SendButton");



            // allow typing when selected
            this.input.Selected = true;
            Game1.keyboardDispatcher.Subscriber = this.input;


            RecomputeContentHeight();
            ScrollToBottom();
        }


        public void CloseMenu(bool playSound)
        {
            if (Game1.keyboardDispatcher?.Subscriber == this.input)
            {
                Game1.keyboardDispatcher.Subscriber = null;
            }

            this.exitThisMenu(playSound);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {

            // close the menu
            if (this.upperRightCloseButton != null && this.upperRightCloseButton.containsPoint(x, y))
            {
                this.CloseMenu(playSound);
                Game1.playSound("bigDeSelect");
                return;
            }


            //send the chat
            if (this.sendButton.containsPoint(x, y))
{
                if (!_isSending)
                    _ = this.TrySend(); // fire-and-forget intentionally
                Game1.playSound("coin");
                return;
            }


            // select the input box
            if(this.inputComponent != null && this.inputComponent.containsPoint(x, y))
            {
                this.input.Selected = true;
                Game1.keyboardDispatcher.Subscriber = this.input;
                Game1.playSound("mouseClick");
                return;
            }

            Game1.playSound("mouseClick");
            base.receiveLeftClick(x,y,playSound);

        }


        public override void receiveKeyPress(Microsoft.Xna.Framework.Input.Keys key)
        {
            // Block keys that normally close menus
            if (key == Microsoft.Xna.Framework.Input.Keys.E ||
                key == Microsoft.Xna.Framework.Input.Keys.Escape)
            {
                return;
            }

            base.receiveKeyPress(key);
        }
        private async Task TrySend()
        {
            if (_isSending) return;
            _isSending = true;

            try
            {
                string q = this.input.Text?.Trim() ?? "";
                if (q.Length == 0)
                    return;

                this.history.Add(new ChatMessage("You", q));
                RecomputeContentHeight();
                ScrollToBottom();
                this.input.Text = "";

                // Optional: show a placeholder so user sees progress
                var thinkingMsg = new ChatMessage("Grandpa", "(thinking...)");
                this.history.Add(thinkingMsg);
                RecomputeContentHeight();
                ScrollToBottom();

                string result = await this.rag.GetAnswerAsync(q);

                // Replace "(thinking...)" with actual answer
                thinkingMsg.Message = result;

                RecomputeContentHeight();
                ScrollToBottom();
            }
            catch (Exception ex)
            {
                this.history.Add(new ChatMessage("Grandpa", $"(error) {ex.Message}"));
                RecomputeContentHeight();
                ScrollToBottom();
            }
            finally
            {
                _isSending = false;
            }
        }

        public override void draw(SpriteBatch b)
        {

            // dim the background
            b.Draw(Game1.fadeToBlackRect,
                new Rectangle(0, 0, Game1.viewport.Width, Game1.viewport.Height),
                Color.Black * 0.5f);

            // menu frame
            IClickableMenu.drawTextureBox(
                b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height,
                Color.White
            );



            // close button
            this.upperRightCloseButton?.draw(b);

            // log panel frame
            IClickableMenu.drawTextureBox(
                b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                this.logPanel.X, this.logPanel.Y, this.logPanel.Width, this.logPanel.Height, Color.White
            );

    
            // --- draw chat with scroll + clipping ---
            Rectangle oldScissor = b.GraphicsDevice.ScissorRectangle;
            RasterizerState oldRaster = b.GraphicsDevice.RasterizerState;

            // Need scissor to keep text inside the panel
            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null,
                new RasterizerState { ScissorTestEnable = true });

            var clip = new Rectangle(
                this.logPanel.X + 4,
                this.logPanel.Y + 4,
                this.logPanel.Width - 8,
                this.logPanel.Height - 8
            );
            b.GraphicsDevice.ScissorRectangle = clip;


            int drawY = this.logPanel.Y + 12 - this.scrollOffsetPx;
            int textX = this.logPanel.X + 12;
            int textWidth = this.logPanel.Width - 24;

            foreach (var msg in this.history)
            {
                string header = $"{msg.Role}: ";
                Utility.drawTextWithShadow(b, header, Game1.smallFont, new Vector2(textX, drawY), Game1.textColor);
                drawY += (int)Game1.smallFont.MeasureString("X").Y + HeaderPaddingPx;

                string wrapped = Game1.parseText(msg.Message, Game1.smallFont, textWidth);
                Utility.drawTextWithShadow(b, wrapped, Game1.smallFont, new Vector2(textX, drawY), Game1.textColor);
                drawY += (int)Game1.smallFont.MeasureString(wrapped).Y + MessagePaddingPx;
            }

            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);

            b.GraphicsDevice.ScissorRectangle = oldScissor;
            b.GraphicsDevice.RasterizerState = oldRaster;


            // input + send
            this.input.Draw(b);
            // draw Send button (box + label)
            IClickableMenu.drawTextureBox(
                b,
                Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                this.sendButton.bounds.X,
                this.sendButton.bounds.Y,
                this.sendButton.bounds.Width,
                this.sendButton.bounds.Height,
                Color.White
            );

            string label = "Send";
            Vector2 size = Game1.smallFont.MeasureString(label);

            float labelX = this.sendButton.bounds.X + (this.sendButton.bounds.Width - size.X) / 2f;
            float labelY = this.sendButton.bounds.Y + (this.sendButton.bounds.Height - size.Y) / 2f;

            Utility.drawTextWithShadow(
                b,
                label,
                Game1.smallFont,
                new Vector2(labelX, labelY),
                Game1.textColor
            );


            Utility.drawTextWithShadow(
                b,
                "Click Send to submit | Close with X",
                Game1.smallFont,
                new Vector2(this.xPositionOnScreen + OuterPaddingPx, this.yPositionOnScreen + this.height - FooterHeightPx),
                Color.Gray
            );

            this.drawMouse(b);
            
        }

        private void RecomputeContentHeight()
        {
            int y= 0;
            int textWidth = this.logPanel.Width - 24;

            foreach(ChatMessage msg in history)
            {
                
                y += (int)Game1.smallFont.MeasureString("X").Y + HeaderPaddingPx ;

                string wrapped = Game1.parseText(msg.Message ,Game1.smallFont, textWidth);

                y += (int)Game1.smallFont.MeasureString(wrapped).Y+ MessagePaddingPx;

            }

            this.contentHeightPx = y;
            ClampScroll();
        }

        private void ClampScroll()
        {
            int maxScroll = Math.Max(0, contentHeightPx - this.logPanel.Height);
            this.scrollOffsetPx = Math.Clamp(this.scrollOffsetPx, 0, maxScroll);
        }

        private void ScrollToBottom()
        {
            int maxScroll = Math.Max(0, contentHeightPx - this.logPanel.Height);
            this.scrollOffsetPx = maxScroll;
        }

        public override void receiveScrollWheelAction(int direction)
        {
            if (!this.logPanel.Contains(Game1.getOldMouseX(), Game1.getOldMouseY()))
                return;
            int delta = -Math.Sign(direction) * ScrollStepPx; 
            this.scrollOffsetPx += delta;
            ClampScroll();
        }
    }
}