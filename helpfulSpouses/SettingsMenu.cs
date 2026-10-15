using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace helpfulSpouses
{
    // Menú de ajustes del mod, abierto desde el NPC.
    // ponytail: cascarón por ahora; las opciones se agregan cuando se definan.
    internal sealed class SettingsMenu : IClickableMenu
    {
        const int MenuWidth = 800;
        const int MenuHeight = 600;

        public SettingsMenu()
            : base(Game1.uiViewport.Width / 2 - MenuWidth / 2,
                   Game1.uiViewport.Height / 2 - MenuHeight / 2,
                   MenuWidth, MenuHeight, showUpperRightCloseButton: true)
        {
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                if (playSound) Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }
            base.receiveLeftClick(x, y, playSound);
        }

        public override void draw(SpriteBatch b)
        {
            // Oscurece el fondo
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.4f);

            // Caja del menú
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // Título (se localizará cuando definamos las opciones)
            string title = "Ajustes";
            Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + width / 2 - titleSize.X / 2, yPositionOnScreen + 48),
                Game1.textColor);

            base.draw(b); // botón de cerrar
            drawMouse(b);
        }
    }
}
