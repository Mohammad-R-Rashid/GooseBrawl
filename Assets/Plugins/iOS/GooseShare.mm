// iOS share sheet for GOOSED.: the photo with the goose (AirDrop, Messages, Save Image, ...).
// Called from Assets/Scripts/Core/GooseShareService.cs via [DllImport("__Internal")]. Everything runs on the main queue.
#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import "UnityAppController.h"

extern "C" {

void GooseShare_Image(const char *pngPath, const char *text)
{
    NSString *path = pngPath ? [NSString stringWithUTF8String:pngPath] : nil;
    NSString *body = text ? [NSString stringWithUTF8String:text] : @"";
    dispatch_async(dispatch_get_main_queue(), ^{
        NSMutableArray *items = [NSMutableArray array];
        UIImage *image = path.length ? [UIImage imageWithContentsOfFile:path] : nil;
        if (image) [items addObject:image];
        if (body.length) [items addObject:body];
        UIViewController *root = UnityGetGLViewController();
        if (items.count == 0 || root == nil) { NSLog(@"[GooseShare] nothing to share (image=%d root=%d)", image != nil, root != nil); return; }
        UIActivityViewController *sheet = [[UIActivityViewController alloc] initWithActivityItems:items applicationActivities:nil];
        sheet.excludedActivityTypes = @[UIActivityTypeAssignToContact, UIActivityTypePrint, UIActivityTypeAddToReadingList];
        // iPad presents this as a popover and crashes without an anchor.
        UIPopoverPresentationController *pop = sheet.popoverPresentationController;
        if (pop) {
            pop.sourceView = root.view;
            pop.sourceRect = CGRectMake(CGRectGetMidX(root.view.bounds), CGRectGetMaxY(root.view.bounds) - 80.0, 1.0, 1.0);
            pop.permittedArrowDirections = UIPopoverArrowDirectionDown;
        }
        [root presentViewController:sheet animated:YES completion:nil];
    });
}

}
